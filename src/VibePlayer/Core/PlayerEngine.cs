using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace VibePlayer.Core;

public readonly record struct TrackInfo(int Id, string Name);

/// <summary>
/// Thin wrapper around LibVLC (the engine inside VLC). All LibVLC events are marshalled to the UI thread,
/// and the calls that can block (open/stop) run on a background queue so the window never freezes.
/// </summary>
public sealed class PlayerEngine
{
    private readonly Dispatcher _ui;
    private readonly object _queueGate = new();
    private Task _queue = Task.CompletedTask;

    private LibVLC? _vlc;
    private MediaPlayer? _player;
    private Media? _media;
    private volatile bool _loading;

    public PlayerEngine(Dispatcher ui) => _ui = ui;

    public event Action? Opening;
    public event Action? Playing;
    public event Action? Paused;
    public event Action? Stopped;
    public event Action? EndReached;
    public event Action? Error;
    public event Action? VideoOutputChanged;
    public event Action? TracksChanged;
    public event Action? LengthChanged;

    public bool IsReady => _player != null;
    public string VlcVersion => _vlc?.Version ?? "";
    public float BufferingPercent { get; private set; } = 100f;
    public DateTime LastBufferingUtc { get; private set; }
    public bool IsLoading => _loading;

    private static string LibDirectory => Path.Combine(AppContext.BaseDirectory, "libvlc", LibFolderName);

    /// <summary>The VLC engine that matches this process: libvlc\win-x64, win-x86 or win-arm64.</summary>
    private static string LibFolderName => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => "win-x86",
        Architecture.Arm64 => "win-arm64",
        _ => "win-x64",
    };

    private static string PluginCacheFile => Path.Combine(LibDirectory, "plugins", "plugins.dat");

    private static readonly object NativeGate = new();
    private static bool s_nativeLoaded;

    private static void LoadNativeLibrary()
    {
        lock (NativeGate)
        {
            if (s_nativeLoaded) return;
            if (Directory.Exists(LibDirectory)) LibVLCSharp.Shared.Core.Initialize(LibDirectory);
            else LibVLCSharp.Shared.Core.Initialize();
            s_nativeLoaded = true;
        }
    }

    /// <summary>Loads libvlc for other users of it (the AI scanner); safe to call any number of times.</summary>
    public static void EnsureNativeLibrary() => LoadNativeLibrary();

    /// <summary>
    /// Writes plugins.dat (what VLC's own installer does with vlc-cache-gen). Without it LibVLC has to
    /// open all ~320 plugin DLLs on every start, which can take minutes while antivirus scans them.
    /// </summary>
    public static bool BuildPluginCache()
    {
        try
        {
            LoadNativeLibrary();
            using var vlc = new LibVLC("--reset-plugins-cache", "--quiet");
            return File.Exists(PluginCacheFile);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BuildPluginCache");
            return false;
        }
    }

    private static bool CanWritePluginCache()
    {
        try
        {
            var probe = Path.Combine(LibDirectory, "plugins", ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task InitializeAsync(IntPtr videoHwnd) => Task.Run(() =>
    {
        LoadNativeLibrary();

        var options = new List<string>
        {
            "--no-video-title-show",   // we draw our own title
            "--no-snapshot-preview",
            "--no-stats",
            "--quiet",
        };
        // Portable copies / dev builds: create the plugin cache on first run so later starts are instant.
        if (!File.Exists(PluginCacheFile) && CanWritePluginCache()) options.Add("--reset-plugins-cache");

        var vlc = new LibVLC(options.ToArray());

        var player = new MediaPlayer(vlc)
        {
            EnableKeyInput = false,    // keyboard & mouse are handled by our own UI
            EnableMouseInput = false,
            Hwnd = videoHwnd,
        };

        player.Opening += (_, _) => Post(() => Opening?.Invoke());
        player.Playing += (_, _) => { _loading = false; Post(() => Playing?.Invoke()); };
        player.Paused += (_, _) => Post(() => Paused?.Invoke());
        player.Stopped += (_, _) => { if (!_loading) Post(() => Stopped?.Invoke()); };
        player.EndReached += (_, _) => { _loading = false; Post(() => EndReached?.Invoke()); };
        player.EncounteredError += (_, _) => { _loading = false; Post(() => Error?.Invoke()); };
        player.Buffering += (_, e) => { BufferingPercent = e.Cache; LastBufferingUtc = DateTime.UtcNow; };
        player.Vout += (_, _) => Post(() => VideoOutputChanged?.Invoke());
        player.ESAdded += (_, _) => Post(() => TracksChanged?.Invoke());
        player.ESDeleted += (_, _) => Post(() => TracksChanged?.Invoke());
        player.ESSelected += (_, _) => Post(() => TracksChanged?.Invoke());
        player.LengthChanged += (_, _) => Post(() => LengthChanged?.Invoke());

        _vlc = vlc;
        _player = player;
    });

    private void Post(Action action) => _ui.BeginInvoke(action);

    private void Enqueue(Action work)
    {
        lock (_queueGate)
        {
            _queue = _queue.ContinueWith(_ =>
            {
                try { work(); }
                catch (Exception ex) { Log.Error(ex, "PlayerEngine queue"); }
            }, TaskScheduler.Default);
        }
    }

    // ---------------------------------------------------------------- playback

    public void Load(MediaItem item, long startMs)
    {
        var vlc = _vlc;
        var player = _player;
        if (vlc == null || player == null) return;

        _loading = true;
        BufferingPercent = 100f;
        Enqueue(() =>
        {
            var media = item.IsUrl
                ? new Media(vlc, item.Source, FromType.FromLocation)
                : new Media(vlc, item.Source, FromType.FromPath);

            // VLC 3 reads ~1 s ahead by default. Speed changes (hold for 2x) only take full effect once
            // that buffer has played out, so keep it short for local disks; network shares keep the default.
            if (!item.IsUrl && !IsNetworkPath(item.Source))
                media.AddOption(":file-caching=300");

            if (startMs > 0)
                media.AddOption(":start-time=" + (startMs / 1000.0).ToString("0.000", CultureInfo.InvariantCulture));

            var old = _media;
            _media = media;
            player.Play(media);
            old?.Dispose();
        });
    }

    private static bool IsNetworkPath(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch
        {
            return true; // unknown: be conservative
        }
    }

    public void Stop()
    {
        var player = _player;
        if (player == null) return;
        _loading = false;
        Enqueue(() => player.Stop());
    }

    public VLCState State => _player?.State ?? VLCState.NothingSpecial;
    public bool IsPlaying => _player?.IsPlaying ?? false;

    public void SetPause(bool pause)
    {
        if (_player == null) return;
        _player.SetPause(pause);
    }

    public long Time
    {
        get => _player?.Time ?? 0;
        set { if (_player != null && _player.IsSeekable) _player.Time = Math.Max(0, value); }
    }

    public long Length => _player?.Length ?? 0;
    public bool IsSeekable => _player?.IsSeekable ?? false;

    public float Rate => _player?.Rate ?? 1f;
    public void SetRate(float rate) => _player?.SetRate(rate);

    public int Volume
    {
        get => _player?.Volume ?? 100;
        set { if (_player != null) _player.Volume = MathEx.Clamp(value, 0, 200); }
    }

    public bool Mute
    {
        get => _player?.Mute ?? false;
        set { if (_player != null) _player.Mute = value; }
    }

    public void NextFrame() => _player?.NextFrame();

    // ---------------------------------------------------------------- tracks

    public IReadOnlyList<TrackInfo> AudioTracks =>
        _player?.AudioTrackDescription.Where(t => t.Id >= 0).Select(t => new TrackInfo(t.Id, Clean(t.Name, "Track"))).ToList()
        ?? new List<TrackInfo>();

    public int AudioTrack => _player?.AudioTrack ?? -1;
    public void SetAudioTrack(int id) => _player?.SetAudioTrack(id);

    public IReadOnlyList<TrackInfo> SubtitleTracks =>
        _player?.SpuDescription.Where(t => t.Id >= 0).Select(t => new TrackInfo(t.Id, Clean(t.Name, "Subtitle"))).ToList()
        ?? new List<TrackInfo>();

    public int Subtitle => _player?.Spu ?? -1;
    public void SetSubtitle(int id) => _player?.SetSpu(id);

    public bool AddSubtitleFile(string path)
    {
        if (_player == null) return false;
        return _player.AddSlave(MediaSlaveType.Subtitle, new Uri(path).AbsoluteUri, true);
    }

    /// <summary>Subtitle sync offset in milliseconds (positive = subtitles later).</summary>
    public long SubtitleDelayMs
    {
        get => (_player?.SpuDelay ?? 0) / 1000;
        set => _player?.SetSpuDelay(value * 1000);
    }

    private static string Clean(string? name, string fallback) =>
        string.IsNullOrWhiteSpace(name) ? fallback : name!.Trim();

    // ---------------------------------------------------------------- video

    public string? AspectRatio
    {
        get => _player?.AspectRatio;
        set { if (_player != null) _player.AspectRatio = value; }
    }

    /// <summary>Picture brightness, 1.0 = original (VLC's "adjust" video filter, GPU accelerated).</summary>
    public void SetBrightness(float value)
    {
        if (_player == null) return;
        // Leave the filter off until it's actually needed; once on, keep it on so changes don't flicker.
        if (!_adjustEnabled && Math.Abs(value - 1f) < 0.001f) return;
        _player.SetAdjustInt(VideoAdjustOption.Enable, 1);
        _player.SetAdjustFloat(VideoAdjustOption.Brightness, MathEx.Clamp(value, 0f, 2f));
        _adjustEnabled = true;
    }

    private bool _adjustEnabled;

    public int VideoTrackCount => _player?.VideoTrackCount ?? 0;

    public (uint Width, uint Height) VideoSize
    {
        get
        {
            uint w = 0, h = 0;
            if (_player != null && _player.Size(0, ref w, ref h)) return (w, h);
            return (0, 0);
        }
    }

    public bool TakeSnapshot(string path) => _player?.TakeSnapshot(0, path, 0, 0) ?? false;

    public string? GetMeta(MetadataType type)
    {
        try { return _media?.Meta(type); }
        catch { return null; }
    }

    /// <summary>Stops playback so the native video window can be torn down safely.</summary>
    public void Shutdown()
    {
        var player = _player;
        if (player == null) return;
        try
        {
            Task.Run(() => player.Stop()).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Shutdown");
        }
    }
}
