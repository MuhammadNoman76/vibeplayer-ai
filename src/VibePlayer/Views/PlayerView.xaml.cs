using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using VibePlayer.Controls;
using VibePlayer.Core;
using VibePlayer.Interop;

namespace VibePlayer.Views;

public partial class PlayerView : UserControl
{
    private static readonly float[] Speeds = { 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f, 2.5f, 3f };

    private static readonly (string Label, string? Value)[] AspectRatios =
    {
        ("Default", null), ("16:9", "16:9"), ("4:3", "4:3"), ("21:9", "21:9"), ("2.35:1", "235:100"),
        ("16:10", "16:10"), ("1:1", "1:1"), ("5:4", "5:4"),
    };

    private const float HoldRate = 2f;
    private const int MaxVolume = 150;

    private readonly IPlayerHost _host;
    private readonly AppSettings _settings;
    private readonly PlayerEngine _engine;

    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _holdTimer;
    private readonly DispatcherTimer _infoTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _seekBubbleTimer;
    private readonly DispatcherTimer _audioCheckTimer;

    private Playlist _playlist = Playlist.Empty;
    private MediaItem? _current;
    private bool _hasMedia;
    private bool _ended;
    private bool _playWhenReady;
    private bool _hasVideo;
    private readonly List<string> _pendingSubtitles = new();

    // Explorer starts one process per selected file; arrivals close together become one playlist.
    private readonly List<string> _externalBatch = new();
    private DateTime _lastExternalOpen = DateTime.MinValue;

    // Mouse on the video
    private bool _leftDown;
    private bool _rightDown;
    private bool _holding;
    private Point _lastMouseScreen = new(double.NaN, double.NaN);

    // Timeline
    private bool _seekDragging;
    private long _dragMs;
    private DateTime _lastDragSeek;
    private long _seekTargetMs = -1;
    private DateTime _seekTargetUntil;
    private long _lastTime;
    private long _lastLength;
    private long _shownTime;

    // Misc state
    private bool _volumeDragging;
    private bool _controlsVisible = true;
    private bool _settingsOpen;
    private float _rate = 1f;
    private int _lastSubtitleId = -1;
    private DateTime? _busySince;
    private bool _spinnerVisible;
    private uint _executionState;
    private int _slowTick;
    private DateTime _lastSettingsSave = DateTime.UtcNow;
    private int _seekBubbleSeconds;
    private int _seekBubbleDirection;

    private readonly Dictionary<FrameworkElement, Func<string>> _tips = new();
    private FrameworkElement? _tipOwner;

    // Skip ranges ("don't show me 34:05 - 44:54")
    private List<SkipRange> _skips = new();
    private readonly DispatcherTimer _skipTimer;
    private long? _pendingSkipStart;
    private long _lastSkipEndMs = -1;
    private DateTime _lastSkipAt;
    private double _skipMarksWidth;
    private long _skipMarksLength = -1;

    // Mouse wheel
    private int _wheelAccumulator;
    private bool _wheelSeeks;

    public PlayerView(IPlayerHost host, AppSettings settings)
    {
        _host = host;
        _settings = settings;
        InitializeComponent();

        _engine = new PlayerEngine(Dispatcher);
        _engine.Playing += OnEnginePlaying;
        _engine.Paused += UpdatePlayState;
        _engine.Stopped += UpdatePlayState;
        _engine.EndReached += OnEngineEndReached;
        _engine.Error += OnEngineError;
        _engine.VideoOutputChanged += OnVideoOutputChanged;
        _engine.TracksChanged += UpdateCcState;
        _engine.LengthChanged += () => { _lastLength = Math.Max(_lastLength, _engine.Length); RefreshTimeline(); };

        _tick = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(200) };
        _tick.Tick += OnTick;
        _tick.Start();

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); HideControls(); };

        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer.Stop();
            if (_leftDown || _rightDown) BeginHold();
        };

        _infoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _infoTimer.Tick += (_, _) =>
        {
            _infoTimer.Stop();
            if (!_holding) Fade(InfoPill, 0, 250);
        };

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Fade(Toast, 0, 300); };

        _seekBubbleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _seekBubbleTimer.Tick += (_, _) =>
        {
            _seekBubbleTimer.Stop();
            _seekBubbleSeconds = 0;
            _seekBubbleDirection = 0;
            Fade(SeekBubbleLeft, 0, 250);
            Fade(SeekBubbleRight, 0, 250);
        };

        _audioCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _audioCheckTimer.Tick += (_, _) => { _audioCheckTimer.Stop(); CheckAudioOnly(); };

        // Checked often so at most a frame or two of a skipped part is ever visible.
        _skipTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(40) };
        _skipTimer.Tick += (_, _) => CheckSkip();
        _skipTimer.Start();

        WireEvents();
        ApplyAutoplayVisual();
        UpdateVolumeVisual();
        RefreshRecent();
        BuildShortcuts();
        SetHasMedia(false);
        InitUpdates();

        Root.SizeChanged += (_, _) =>
        {
            UpdateResponsiveLayout();
            RefreshTimeline();
        };
    }

    // =====================================================================================
    //  Startup / opening media
    // =====================================================================================

    public async void Start(IntPtr videoHwnd, string[] startupArgs)
    {
        if (startupArgs.Length > 0) OpenFromExternal(startupArgs);

        // Normally instant; only say something if the engine is slow to come up (e.g. first run without a plugin cache).
        var tip = WelcomeTip.Text;
        var slowStart = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        slowStart.Tick += (_, _) =>
        {
            slowStart.Stop();
            if (_engine.IsReady) return;
            WelcomeTip.Text = "Starting the VLC engine… the very first start can take a moment.";
            if (_hasMedia) ShowToast("Starting the VLC engine…", 30);
        };
        slowStart.Start();

        try
        {
            await _engine.InitializeAsync(videoHwnd);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Engine init");
            ShowToast("Couldn't start the VLC engine: " + ex.Message, 10);
            return;
        }
        finally
        {
            slowStart.Stop();
            WelcomeTip.Text = tip;
        }
        if (Toast.Opacity > 0 && ToastText.Text.StartsWith("Starting the VLC engine", StringComparison.Ordinal)) Fade(Toast, 0, 200);

        _engine.Volume = _settings.Volume;
        _engine.Mute = _settings.Muted;
        AboutText.Text = AboutLine();

        if (_playWhenReady)
        {
            _playWhenReady = false;
            PlayCurrent();
        }
    }

    /// <summary>Files from the command line / "Open with" / another instance.</summary>
    public void OpenFromExternal(string[] args)
    {
        var inputs = args.Select(a => a.Trim().Trim('"'))
            .Where(a => a.Length > 0 && !a.StartsWith("--", StringComparison.Ordinal))
            .ToList();
        if (inputs.Count == 0) return;

        var files = new List<string>();
        foreach (var input in inputs)
        {
            if (MediaFormats.LooksLikeUrl(input)) continue;
            try
            {
                var full = Path.GetFullPath(input);
                if (File.Exists(full) && MediaFormats.KindOf(full) != MediaKind.Subtitle) files.Add(full);
            }
            catch
            {
                // ignore malformed paths
            }
        }

        var now = DateTime.UtcNow;
        var merge = files.Count == inputs.Count && _externalBatch.Count > 0 && (now - _lastExternalOpen).TotalSeconds < 2.0;
        _lastExternalOpen = now;

        if (merge)
        {
            foreach (var f in files)
                if (!_externalBatch.Contains(f, StringComparer.OrdinalIgnoreCase)) _externalBatch.Add(f);

            var currentSource = _playlist.Current?.Source ?? _externalBatch[0];
            var sorted = _externalBatch.OrderBy(f => f, MediaFormats.NaturalComparer).ToList();
            var index = sorted.FindIndex(f => string.Equals(f, currentSource, StringComparison.OrdinalIgnoreCase));
            _playlist = new Playlist(sorted.Select(f => new MediaItem(f, false)), Math.Max(0, index));
            UpdateNavButtons();
            return;
        }

        _externalBatch.Clear();
        _externalBatch.AddRange(files);
        OpenInputs(inputs);
    }

    private void OpenInputs(IReadOnlyList<string> inputs)
    {
        var items = new List<MediaItem>();
        var subtitles = new List<string>();

        foreach (var raw in inputs)
        {
            var input = raw.Trim().Trim('"');
            if (input.Length == 0) continue;

            if (MediaFormats.LooksLikeUrl(input))
            {
                items.Add(new MediaItem(input, true));
                continue;
            }

            string full;
            try { full = Path.GetFullPath(input); }
            catch { continue; }

            if (Directory.Exists(full))
                items.AddRange(Playlist.EnumerateFolder(full).Select(f => new MediaItem(f, false)));
            else if (File.Exists(full))
            {
                if (MediaFormats.KindOf(full) == MediaKind.Subtitle) subtitles.Add(full);
                else items.Add(new MediaItem(full, false));
            }
        }

        if (items.Count == 0)
        {
            if (subtitles.Count > 0 && _hasMedia) subtitles.ForEach(AddSubtitle);
            else if (subtitles.Count > 0) ShowToast("Open a video first, then drop the subtitle file on it.");
            else ShowToast("Nothing playable was found.");
            return;
        }

        SaveResumePosition();

        if (items.Count == 1 && !items[0].IsUrl)
            _playlist = Playlist.FromFolderOf(items[0].Source);
        else if (items.All(i => !i.IsUrl) && inputs.All(i => !Directory.Exists(i)))
            _playlist = new Playlist(items.OrderBy(i => i.Source, MediaFormats.NaturalComparer));
        else
            _playlist = new Playlist(items);

        _pendingSubtitles.Clear();
        _pendingSubtitles.AddRange(subtitles);
        PlayCurrent();
    }

    private void PlayCurrent()
    {
        var item = _playlist.Current;
        if (item == null) return;

        _current = item;
        _ended = false;
        _hasVideo = false;
        _lastTime = 0;
        _lastLength = 0;
        _shownTime = 0;
        _seekTargetMs = -1;

        SetHasMedia(true);
        _host.SetMediaTitle(item.DisplayName);
        QualityBadge.Visibility = Visibility.Collapsed;
        AudioPanel.Visibility = Visibility.Collapsed;
        CcUnderline.Visibility = Visibility.Collapsed;
        ResetArtwork();
        UpdateNavButtons();
        RefreshTimeline();

        _settings.AddRecent(item.Source);
        _skips = item.IsUrl ? new List<SkipRange>() : _settings.GetSkips(item.Source).ToList();
        _watchOnce = null;
        _pendingSkipStart = null;
        _lastSkipEndMs = -1;
        AiLoad(item);
        DrawSkipMarks();

        if (!_engine.IsReady)
        {
            _playWhenReady = true;
            return;
        }

        var resume = item.IsUrl ? 0 : _settings.GetResume(item.Source);

        // Don't even start inside a skipped part (e.g. a skip from 0:00).
        SkipRange? skippedAtStart = null;
        foreach (var (range, _) in ActiveSkips().OrderBy(s => s.Range.StartMs))
        {
            if (resume >= range.StartMs && resume < range.EndMs)
            {
                skippedAtStart = range;
                resume = range.EndMs;
            }
        }

        _engine.Load(item, resume);
        AiStartIfNeeded(resume);
        if (skippedAtStart != null)
        {
            _shownTime = resume;
            if (_settings.ShowSkipNotice)
                ShowToast($"Skipped {FormatTime(skippedAtStart.StartMs, skippedAtStart.EndMs)} – {FormatTime(skippedAtStart.EndMs, skippedAtStart.EndMs)}", 2.5);
        }
        else if (resume > 0)
        {
            _shownTime = resume;
            ShowToast($"Resumed from {FormatTime(resume, resume)}  ·  press 0 to start over");
        }

        UpdatePlayState();
        ShowControls();
    }

    public void PlayNext()
    {
        if (!_playlist.HasNext) return;
        SaveResumePosition();
        _playlist.MoveNext();
        PlayCurrent();
    }

    public void PlayPrevious()
    {
        if (!_hasMedia) return;
        if (_playlist.HasPrevious && _lastTime < 5000)
        {
            SaveResumePosition();
            _playlist.MovePrevious();
            PlayCurrent();
        }
        else
        {
            SeekTo(0);
        }
    }

    private void Replay()
    {
        if (_current == null) return;
        _ended = false;
        _engine.Load(_current, 0);
        UpdatePlayState();
    }

    private void CloseMedia()
    {
        if (!_hasMedia) return;
        SaveResumePosition();
        if (_holding) EndHold();
        _engine.Stop();
        AiCloseMedia();
        _playlist = Playlist.Empty;
        _current = null;
        SetHasMedia(false);
        UpdatePlayState();
    }

    private void SetHasMedia(bool has)
    {
        _hasMedia = has;
        WelcomePanel.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        Chrome.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has)
        {
            AudioPanel.Visibility = Visibility.Collapsed;
            ShowSpinner(false);
            Root.Cursor = null;
            CloseSettings();
            _host.SetMediaTitle(null);
            RefreshRecent();
            UpdateExecutionState(false);
        }
        _host.UpdateTaskbar(has, false, _playlist.HasPrevious, _playlist.HasNext);
    }

    // =====================================================================================
    //  Engine events
    // =====================================================================================

    private void OnEnginePlaying()
    {
        _ended = false;
        // A new audio output is created per file: re-apply the user's volume & speed.
        _engine.Volume = _settings.Volume;
        _engine.Mute = _settings.Muted;
        _engine.SetBrightness(_settings.Brightness / 100f);
        var wanted = _holding ? HoldRate : _rate;
        if (Math.Abs(_engine.Rate - wanted) > 0.01f) _engine.SetRate(wanted);

        if (_pendingSubtitles.Count > 0)
        {
            var subs = _pendingSubtitles.ToList();
            _pendingSubtitles.Clear();
            subs.ForEach(AddSubtitle);
        }

        UpdatePlayState();
        UpdateCcState();
        _audioCheckTimer.Stop();
        _audioCheckTimer.Start();
        RestartHideTimer();
    }

    private void OnEngineEndReached()
    {
        if (_current is { IsUrl: false }) _settings.SetResume(_current.Source, 0);
        if (_holding) EndHold();

        if (_settings.Loop && _current != null)
        {
            _engine.Load(_current, 0);
            return;
        }

        if (_settings.Autoplay && _playlist.HasNext)
        {
            _playlist.MoveNext();
            PlayCurrent();
            return;
        }

        _ended = true;
        _shownTime = _lastLength;
        UpdatePlayState();
        ShowControls();
        RefreshTimeline();
    }

    private void OnEngineError()
    {
        var failed = _current;
        ShowToast($"Can't play \"{failed?.DisplayName}\". The file may be damaged or in an unsupported format.", 5);
        _ended = true;
        UpdatePlayState();
        ShowControls();

        if (_settings.Autoplay && _playlist.HasNext)
        {
            var skip = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            skip.Tick += (_, _) =>
            {
                skip.Stop();
                if (ReferenceEquals(_current, failed)) PlayNext();
            };
            skip.Start();
        }
    }

    private void OnVideoOutputChanged()
    {
        _engine.SetBrightness(_settings.Brightness / 100f);
        if (_engine.VideoTrackCount > 0)
        {
            _hasVideo = true;
            AudioPanel.Visibility = Visibility.Collapsed;
        }
        UpdateQualityBadge();
        UpdateExecutionState(_engine.IsPlaying);
    }

    private void CheckAudioOnly()
    {
        if (!_hasMedia || _ended) return;
        if (_engine.VideoTrackCount > 0)
        {
            _hasVideo = true;
            AudioPanel.Visibility = Visibility.Collapsed;
            UpdateQualityBadge();
            return;
        }

        _hasVideo = false;
        AudioPanel.Visibility = Visibility.Visible;
        RefreshAudioMeta();
    }

    private void RefreshAudioMeta()
    {
        var title = _engine.GetMeta(MetadataType.Title);
        var artist = _engine.GetMeta(MetadataType.Artist);
        var album = _engine.GetMeta(MetadataType.Album);
        AudioTitle.Text = string.IsNullOrWhiteSpace(title) ? _current?.DisplayName ?? "" : title;
        AudioArtist.Text = string.Join("  ·  ", new[] { artist, album }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var art = _engine.GetMeta(MetadataType.ArtworkURL);
        if (ArtIcon.Visibility == Visibility.Visible && art != null &&
            Uri.TryCreate(art, UriKind.Absolute, out var uri) && uri.IsFile && File.Exists(uri.LocalPath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 400;
                bmp.UriSource = uri;
                bmp.EndInit();
                bmp.Freeze();
                ArtBorder.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                ArtIcon.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Artwork");
            }
        }
    }

    private void ResetArtwork()
    {
        ArtIcon.Visibility = Visibility.Visible;
        ArtBorder.Background = new LinearGradientBrush(Color.FromRgb(0xFF, 0x2D, 0x46), Color.FromRgb(0xDE, 0x14, 0x78), 45);
    }

    // =====================================================================================
    //  Periodic UI refresh
    // =====================================================================================

    private void OnTick(object? sender, EventArgs e)
    {
        if (_updateDeferred || UpdateCard.Visibility == Visibility.Visible) UpdateCardTick();
        if (!_hasMedia) return;
        if (!_engine.IsReady)
        {
            ShowSpinner(true);
            return;
        }

        var length = _engine.Length;
        if (length > 0) _lastLength = length;

        if (_ended)
        {
            _lastTime = _lastLength;
        }
        else
        {
            var time = _engine.Time;
            if (time >= 0 && !_engine.IsLoading) _lastTime = time;
        }

        if (_seekTargetMs >= 0 && (DateTime.UtcNow >= _seekTargetUntil || Math.Abs(_lastTime - _seekTargetMs) < 600))
            _seekTargetMs = -1;

        if (_seekDragging) _shownTime = _dragMs;
        else if (_seekTargetMs >= 0) _shownTime = _seekTargetMs;
        else if (!_engine.IsLoading || _ended) _shownTime = _lastTime;

        // Redrawing a hidden overlay costs CPU on big screens - only refresh what is visible.
        if (_controlsVisible) RefreshTimeline();

        UpdateBusyIndicator();
        AiTick();

        if (++_slowTick % 5 == 0)
        {
            UpdatePlayState();
            UpdateCcState();
            if (_hasVideo && QualityBadge.Visibility != Visibility.Visible) UpdateQualityBadge();
            if (AudioPanel.Visibility == Visibility.Visible) RefreshAudioMeta();
            SaveResumePosition();

            if ((DateTime.UtcNow - _lastSettingsSave).TotalSeconds > 60)
            {
                _lastSettingsSave = DateTime.UtcNow;
                _settings.Save();
            }
        }
    }

    private void RefreshTimeline()
    {
        var length = _lastLength;
        var isLive = _current?.IsUrl == true && length <= 0;
        ProgressArea.Visibility = isLive ? Visibility.Hidden : Visibility.Visible;
        TimeText.Text = isLive
            ? FormatTime(_shownTime, _shownTime)
            : _settings.ShowRemainingTime && length > 0
                ? $"-{FormatTime(Math.Max(0, length - _shownTime), length)} / {FormatTime(length, length)}"
                : $"{FormatTime(_shownTime, length)} / {FormatTime(length, length)}";

        var width = TrackGrid.ActualWidth;
        if (width <= 0) return;
        var fraction = length > 0 ? MathEx.Clamp((double)_shownTime / length, 0, 1) : 0;
        PlayedFill.Width = width * fraction;
        Thumb.Margin = new Thickness(width * fraction - Thumb.Width / 2, 0, 0, 0);

        if (Math.Abs(width - _skipMarksWidth) > 0.5 || length != _skipMarksLength) DrawSkipMarks();
    }

    /// <summary>Segments on the progress bar for the parts that will be skipped: yellow = yours, pink = found by the AI.</summary>
    private void DrawSkipMarks()
    {
        SkipMarks.Children.Clear();
        _skipMarksWidth = TrackGrid.ActualWidth;
        _skipMarksLength = _lastLength;
        if (_skipMarksLength <= 0 || _skipMarksWidth <= 0) return;

        var brush = new SolidColorBrush(Color.FromArgb(_settings.SkipEnabled ? (byte)0xE6 : (byte)0x66, 0xFF, 0xC4, 0x00));
        var aiBrush = new SolidColorBrush(Color.FromArgb(_settings.AiFilter ? (byte)0xE6 : (byte)0x66, AiMarkColor.R, AiMarkColor.G, AiMarkColor.B));
        foreach (var (range, ai) in _skips.Select(r => (r, false)).Concat(_aiSkips.Select(r => (r, true))))
        {
            var fill = ai ? aiBrush : brush;
            var x1 = _skipMarksWidth * MathEx.Clamp((double)range.StartMs / _skipMarksLength, 0, 1);
            var x2 = _skipMarksWidth * MathEx.Clamp((double)range.EndMs / _skipMarksLength, 0, 1);
            var mark = new System.Windows.Shapes.Rectangle { Width = Math.Max(2, x2 - x1), Height = 20, Fill = fill };
            mark.SetBinding(HeightProperty, new System.Windows.Data.Binding(nameof(ActualHeight)) { Source = TrackGrid });
            Canvas.SetLeft(mark, x1);
            SkipMarks.Children.Add(mark);
        }
    }

    private void UpdatePlayState()
    {
        var playing = _hasMedia && !_ended && (_engine.IsPlaying || _engine.IsLoading);
        PlayIcon.Data = Icon(_ended ? "Icon.Replay" : playing ? "Icon.Pause" : "Icon.Play");
        _host.UpdateTaskbar(_hasMedia, playing, _playlist.HasPrevious, _playlist.HasNext);
        UpdateExecutionState(playing);
        RefreshTip();
        if (!playing && _hasMedia) ShowControls();
    }

    private void UpdateBusyIndicator()
    {
        var recentBuffering = _engine.BufferingPercent < 100f &&
                              (DateTime.UtcNow - _engine.LastBufferingUtc).TotalMilliseconds < 1500;
        var busy = _hasMedia && !_ended && (_engine.IsLoading || _engine.State == VLCState.Opening || recentBuffering);

        if (busy)
        {
            _busySince ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - _busySince.Value).TotalMilliseconds > 500) ShowSpinner(true);
        }
        else
        {
            _busySince = null;
            ShowSpinner(false);
        }
    }

    private void ShowSpinner(bool show)
    {
        if (show == _spinnerVisible) return;
        _spinnerVisible = show;
        Spinner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        else
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void UpdateQualityBadge()
    {
        var (w, h) = _engine.VideoSize;
        string? label = h >= 2160 || w >= 3840 ? "4K" : h >= 720 || w >= 1280 ? "HD" : null;
        QualityBadge.Visibility = label == null ? Visibility.Collapsed : Visibility.Visible;
        if (label != null) QualityBadgeText.Text = label;
    }

    private void UpdateCcState()
    {
        if (!_engine.IsReady) return;
        CcUnderline.Visibility = _hasMedia && _engine.Subtitle >= 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateExecutionState(bool playing)
    {
        // Keep the screen awake while a video plays (and the PC awake while audio plays).
        var flags = Native.ES_CONTINUOUS;
        if (playing) flags |= Native.ES_SYSTEM_REQUIRED | (_hasVideo ? Native.ES_DISPLAY_REQUIRED : 0);
        if (flags == _executionState) return;
        _executionState = flags;
        Native.SetThreadExecutionState(flags);
    }

    private void UpdateNavButtons()
    {
        var compact = Root.ActualWidth > 0 && Root.ActualWidth / ControlsScale.ScaleX < 700;
        PrevButton.Visibility = _playlist.HasPrevious && !compact ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _playlist.HasNext ? Visibility.Visible : Visibility.Collapsed;
        _host.UpdateTaskbar(_hasMedia, _hasMedia && (_engine.IsPlaying || _engine.IsLoading), _playlist.HasPrevious, _playlist.HasNext);
    }

    public void OnWindowModeChanged()
    {
        FullscreenIcon.Data = Icon(_host.IsFullscreen ? "Icon.FullscreenExit" : "Icon.Fullscreen");
        UpdateResponsiveLayout();
        RefreshTip();
        ShowControls();
    }

    private void UpdateResponsiveLayout()
    {
        var w = Root.ActualWidth;
        if (w <= 0) return;

        double scale;
        if (_host.IsFullscreen) scale = w >= 2400 ? 1.35 : w >= 1700 ? 1.18 : 1.0;
        else scale = w < 520 ? 0.78 : w < 760 ? 0.9 : 1.0;
        ControlsScale.ScaleX = ControlsScale.ScaleY = scale;

        var available = w / scale - 24;
        AutoplayButton.Visibility = available < 700 ? Visibility.Collapsed : Visibility.Visible;
        TimePill.Visibility = available < 520 ? Visibility.Collapsed : Visibility.Visible;
        CcButton.Visibility = available < 440 ? Visibility.Collapsed : Visibility.Visible;
        UpdateNavButtons();
        if (_settingsOpen) PositionSettings();
    }

    // =====================================================================================
    //  Transport actions
    // =====================================================================================

    public void TogglePlay() => TogglePlay(false);

    private void TogglePlay(bool flash)
    {
        if (!_hasMedia || !_engine.IsReady) return;

        if (_ended || _engine.State is VLCState.Ended or VLCState.Stopped or VLCState.Error)
        {
            Replay();
            if (flash) Flash("Icon.Play");
            return;
        }

        if (_engine.IsPlaying)
        {
            _engine.SetPause(true);
            PlayIcon.Data = Icon("Icon.Play");
            if (flash) Flash("Icon.Pause");
        }
        else
        {
            _engine.SetPause(false);
            PlayIcon.Data = Icon("Icon.Pause");
            if (flash) Flash("Icon.Play");
        }
        RefreshTip();
        ShowControls();
    }

    private void SeekTo(long ms)
    {
        if (!_hasMedia || _current == null) return;

        var length = _lastLength;
        ms = length > 0 ? MathEx.Clamp(ms, 0, Math.Max(0, length - 300)) : Math.Max(0, ms);

        if (_ended || _engine.State is VLCState.Ended or VLCState.Stopped or VLCState.Error)
        {
            _ended = false;
            _engine.Load(_current, ms);
            UpdatePlayState();
        }
        else if (_engine.IsSeekable)
        {
            _engine.Time = ms;
        }
        else
        {
            return;
        }

        _seekTargetMs = ms;
        _seekTargetUntil = DateTime.UtcNow.AddMilliseconds(1500);
        _shownTime = ms;
        RefreshTimeline();
        AiOnSeek(ms);
    }

    private void SeekBy(long deltaMs)
    {
        if (!_hasMedia) return;
        var from = _seekTargetMs >= 0 ? _seekTargetMs : _lastTime;
        SeekTo(from + deltaMs);
        ShowSeekBubble(deltaMs);
    }

    private void SeekToFraction(double fraction)
    {
        if (_lastLength <= 0) return;
        SeekTo((long)(_lastLength * MathEx.Clamp(fraction, 0, 1)));
    }

    private void SetRate(float rate, bool showInfo)
    {
        _rate = MathEx.Clamp(rate, 0.25f, 4f);
        if (!_holding) _engine.SetRate(_rate);
        if (showInfo) ShowInfo(null, Math.Abs(_rate - 1f) < 0.01f ? "Normal speed" : $"{_rate:0.##}x");
    }

    private void SetVolume(int volume, bool showInfo)
    {
        volume = MathEx.Clamp(volume, 0, MaxVolume);
        _settings.Volume = volume;
        _engine.Volume = volume;
        if (volume > 0 && _settings.Muted)
        {
            _settings.Muted = false;
            _engine.Mute = false;
        }
        UpdateVolumeVisual();
        if (showInfo) ShowInfo(VolumeIconFor(volume, _settings.Muted), $"{volume}%");
    }

    private void ToggleMute()
    {
        _settings.Muted = !_settings.Muted;
        if (!_settings.Muted && _settings.Volume == 0) _settings.Volume = 30;
        _engine.Volume = _settings.Volume;
        _engine.Mute = _settings.Muted;
        UpdateVolumeVisual();
        Flash(_settings.Muted ? "Icon.VolumeOff" : "Icon.VolumeHigh");
        RefreshTip();
    }

    private void UpdateVolumeVisual()
    {
        var muted = _settings.Muted || _settings.Volume == 0;
        var fraction = muted ? 0 : Math.Min(_settings.Volume, 100) / 100.0;
        var usable = VolumeSlider.Width - VolumeThumb.Width;
        VolumeThumb.Margin = new Thickness(fraction * usable, 0, 0, 0);
        VolumeFill.Width = fraction * usable + VolumeThumb.Width / 2;
        VolumeIcon.Data = Icon(VolumeIconFor(_settings.Volume, _settings.Muted));
    }

    private static string VolumeIconFor(int volume, bool muted) =>
        muted || volume == 0 ? "Icon.VolumeOff" : volume < 50 ? "Icon.VolumeLow" : "Icon.VolumeHigh";

    // ---- press & hold for 2x -----------------------------------------------------------

    private void BeginHold()
    {
        if (!_hasMedia || _holding || _ended) return;
        if (_engine.State is not (VLCState.Playing or VLCState.Paused)) return;

        _holding = true;
        if (!_engine.IsPlaying) _engine.SetPause(false);
        _engine.SetRate(HoldRate);
        UpdatePlayState();

        _infoTimer.Stop();
        InfoIcon.Visibility = Visibility.Collapsed;
        InfoText.Text = "2x";
        InfoChevrons.Visibility = Visibility.Visible;
        var pulse = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(450)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Chevron1.BeginAnimation(OpacityProperty, pulse);
        Chevron2.BeginAnimation(OpacityProperty, pulse.Clone().Also(a => a.BeginTime = TimeSpan.FromMilliseconds(225)));
        Fade(InfoPill, 1, 120);
    }

    private void EndHold()
    {
        if (!_holding) return;
        _holding = false;
        _engine.SetRate(_rate);
        Chevron1.BeginAnimation(OpacityProperty, null);
        Chevron2.BeginAnimation(OpacityProperty, null);
        Fade(InfoPill, 0, 200);
    }

    // ---- tracks -------------------------------------------------------------------------

    private void ToggleSubtitles()
    {
        if (!_hasMedia) return;
        var tracks = _engine.SubtitleTracks;
        var current = _engine.Subtitle;

        if (current >= 0)
        {
            _lastSubtitleId = current;
            _engine.SetSubtitle(-1);
            ShowInfo("Icon.Subtitles", "Subtitles off");
        }
        else if (tracks.Count > 0)
        {
            var pick = tracks.FirstOrDefault(t => t.Id == _lastSubtitleId);
            if (pick == default) pick = tracks[0];
            _engine.SetSubtitle(pick.Id);
            ShowInfo("Icon.Subtitles", pick.Name);
        }
        else
        {
            ShowToast("This video has no subtitles. Drop a subtitle file (.srt, .ass, .vtt…) onto the player to add one.");
        }
        UpdateCcState();
    }

    private void ShiftSubtitles(long deltaMs)
    {
        var delay = _engine.SubtitleDelayMs + deltaMs;
        _engine.SubtitleDelayMs = delay;
        ShowInfo("Icon.Subtitles", delay == 0 ? "Subtitle delay: 0 ms" : $"Subtitle delay: {delay:+#;-#} ms");
    }

    private void CycleSubtitles()
    {
        if (!_hasMedia) return;
        var tracks = _engine.SubtitleTracks;
        if (tracks.Count == 0)
        {
            ShowToast("No subtitle tracks");
            return;
        }
        var ids = new List<int> { -1 };
        ids.AddRange(tracks.Select(t => t.Id));
        var next = ids[(ids.IndexOf(_engine.Subtitle) + 1) % ids.Count];
        _engine.SetSubtitle(next);
        ShowInfo("Icon.Subtitles", next < 0 ? "Subtitles off" : tracks.First(t => t.Id == next).Name);
        UpdateCcState();
    }

    private void CycleAudioTrack()
    {
        if (!_hasMedia) return;
        var tracks = _engine.AudioTracks;
        if (tracks.Count < 2)
        {
            ShowToast(tracks.Count == 0 ? "No audio tracks" : "Only one audio track");
            return;
        }
        var index = tracks.ToList().FindIndex(t => t.Id == _engine.AudioTrack);
        var next = tracks[(index + 1) % tracks.Count];
        _engine.SetAudioTrack(next.Id);
        ShowInfo("Icon.Audio", next.Name);
    }

    private void CycleAspectRatio()
    {
        if (!_hasMedia) return;
        var index = Array.FindIndex(AspectRatios, a => a.Value == _engine.AspectRatio);
        var next = AspectRatios[(index + 1) % AspectRatios.Length];
        _engine.AspectRatio = next.Value;
        ShowInfo("Icon.AspectRatio", "Aspect ratio: " + next.Label);
    }

    private void AddSubtitle(string path)
    {
        if (_engine.AddSubtitleFile(path))
            ShowToast("Subtitles added: " + Path.GetFileName(path));
        else
            ShowToast("Couldn't load subtitles from " + Path.GetFileName(path));
        Dispatcher.BeginInvoke(UpdateCcState, DispatcherPriority.Background);
    }

    private async void TakeSnapshot()
    {
        if (!_hasMedia || !_hasVideo || _current == null)
        {
            ShowToast("There's no video frame to capture.");
            return;
        }

        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), AppInfo.Name);
            Directory.CreateDirectory(dir);
            var safe = string.Concat(_current.DisplayName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            var file = Path.Combine(dir, $"{safe} {FormatTime(_lastTime, _lastLength).Replace(':', '.')} {DateTime.Now:HHmmss}.png");
            var ok = await Task.Run(() => _engine.TakeSnapshot(file));
            ShowToast(ok ? $"Snapshot saved to Pictures\\{AppInfo.Name}" : "Couldn't take a snapshot.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Snapshot");
            ShowToast("Couldn't take a snapshot.");
        }
    }

    // ---- skip ranges -----------------------------------------------------------------------

    private void CheckSkip()
    {
        var any = (_settings.SkipEnabled && _skips.Count > 0) || (_settings.AiFilter && _aiSkips.Count > 0);
        if (!any || !_hasMedia || _ended || _seekDragging || !_engine.IsPlaying) return;

        var time = _engine.Time;
        foreach (var (range, ai) in ActiveSkips())
        {
            if (time < range.StartMs || time >= range.EndMs - 250) continue;
            if (WatchingOnce(range, time)) continue;
            if (range.EndMs == _lastSkipEndMs)
            {
                var since = (DateTime.UtcNow - _lastSkipAt).TotalMilliseconds;
                // Give the jump a moment to land before trying again.
                if (since < 1500) return;
                // VLC reports the keyframe just before the target for a while after a seek (the picture already
                // shows the target): that is a landed jump, not a reason to jump again.
                if (since < 6000 && time >= range.EndMs - 2500) return;
            }

            _lastSkipEndMs = range.EndMs;
            _lastSkipAt = DateTime.UtcNow;
            SeekTo(range.EndMs);
            if (ai)
            {
                ShowInfo("Icon.FastForward", "Skipped a scene (AI filter)");
                Log.Info($"AI filter: skipped {FormatTime(range.StartMs, _lastLength)} – {FormatTime(range.EndMs, _lastLength)} at {time / 1000.0:0.0} s in {_current?.DisplayName}");
            }
            else if (_settings.ShowSkipNotice)
                ShowToast($"Skipped {FormatTime(range.StartMs, _lastLength)} – {FormatTime(range.EndMs, _lastLength)}", 2);
            return;
        }
    }

    /// <summary>Your skips (when "Skip marked parts" is on) and the AI's (when its filter is on).</summary>
    private IEnumerable<(SkipRange Range, bool Ai)> ActiveSkips()
    {
        if (_settings.SkipEnabled)
            foreach (var range in _skips) yield return (range, false);
        if (_settings.AiFilter)
            foreach (var range in _aiSkips) yield return (range, true);
    }

    private bool CanEditSkips()
    {
        if (_current is { IsUrl: false }) return true;
        ShowToast(_current == null ? "Open a video first." : "Skips can be set for files on this PC, not for streams.");
        return false;
    }

    private void AddSkip(long fromMs, long toMs)
    {
        if (!CanEditSkips() || toMs <= fromMs) return;

        // Merge with overlapping or touching ranges.
        _skips = MergeSkips(_skips.Append(new SkipRange { StartMs = fromMs, EndMs = toMs }));
        SaveSkips();
        ShowToast($"Will skip {FormatTime(fromMs, _lastLength)} – {FormatTime(toMs, _lastLength)} every time this video plays");
    }

    private void RemoveSkip(SkipRange range)
    {
        _skips.Remove(range);
        SaveSkips();
        ShowToast($"Removed skip {FormatTime(range.StartMs, _lastLength)} – {FormatTime(range.EndMs, _lastLength)}");
    }

    private void SaveSkips()
    {
        if (_current is { IsUrl: false }) _settings.SetSkips(_current.Source, _skips.ToList());
        _lastSkipEndMs = -1;
        DrawSkipMarks();
    }

    private void MarkSkipStart()
    {
        if (!CanEditSkips()) return;
        _pendingSkipStart = _seekTargetMs >= 0 ? _seekTargetMs : _lastTime;
        ShowToast($"Skip starts at {FormatTime(_pendingSkipStart.Value, _lastLength)}  ·  play to where it should end and press ]", 4);
    }

    private void MarkSkipEnd()
    {
        if (!CanEditSkips()) return;
        if (_pendingSkipStart is not { } start)
        {
            ShowToast("First press [ where the part you want to skip begins.");
            return;
        }
        var end = _seekTargetMs >= 0 ? _seekTargetMs : _lastTime;
        if (end <= start + 500)
        {
            ShowToast("The end must be after the start - play forward a bit and press ] again.");
            return;
        }
        _pendingSkipStart = null;
        AddSkip(start, end);
    }

    private void ShowAddSkipDialog()
    {
        if (!CanEditSkips()) return;
        var from = _pendingSkipStart ?? _lastTime;
        var to = _lastLength > 0 ? Math.Min(from + 30_000, _lastLength) : from + 30_000;
        var dialog = new SkipRangeDialog(from, to, _lastLength) { Owner = _host.HostWindow };
        if (dialog.ShowDialog() == true)
        {
            _pendingSkipStart = null;
            AddSkip(dialog.FromMs, dialog.ToMs);
        }
    }

    private void ToggleSkipping()
    {
        _settings.SkipEnabled = !_settings.SkipEnabled;
        _skipMarksLength = -1;
        DrawSkipMarks();
        ShowInfo("Icon.FastForward", _settings.SkipEnabled ? "Skipping on" : "Skipping off - playing everything");
    }

    // ---- dialogs ------------------------------------------------------------------------

    private void ShowOpenFileDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open media",
            Filter = MediaFormats.OpenDialogFilter,
            Multiselect = true,
        };
        if (dialog.ShowDialog(_host.HostWindow) == true)
        {
            _externalBatch.Clear();
            OpenInputs(dialog.FileNames);
        }
    }

    private void ShowOpenFolderDialog()
    {
        var dialog = new OpenFolderDialog { Title = "Play a folder" };
        if (dialog.ShowDialog(_host.HostWindow) == true)
        {
            _externalBatch.Clear();
            OpenInputs(new[] { dialog.FolderName });
        }
    }

    private void ShowOpenUrlDialog()
    {
        var dialog = new UrlDialog { Owner = _host.HostWindow };
        if (dialog.ShowDialog() == true)
        {
            _externalBatch.Clear();
            OpenInputs(new[] { dialog.Url });
        }
    }

    private void ShowAddSubtitleDialog()
    {
        if (!_hasMedia) return;
        var dialog = new OpenFileDialog { Title = "Add subtitle file", Filter = MediaFormats.SubtitleDialogFilter };
        if (_current is { IsUrl: false }) dialog.InitialDirectory = Path.GetDirectoryName(_current.Source);
        if (dialog.ShowDialog(_host.HostWindow) == true) AddSubtitle(dialog.FileName);
    }

    // =====================================================================================
    //  Resume positions
    // =====================================================================================

    private void SaveResumePosition()
    {
        if (_current == null || _current.IsUrl || !_engine.IsReady) return;
        var length = _lastLength;
        if (length < 120_000) return; // short clips always start from the beginning

        var time = _lastTime;
        if (_ended || time < 10_000 || time > length - 20_000) _settings.SetResume(_current.Source, 0);
        else _settings.SetResume(_current.Source, time);
    }

    public void Shutdown()
    {
        SaveResumePosition();
        _tick.Stop();
        AiShutdown();
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
        _engine.Shutdown();
    }

    // =====================================================================================
    //  Event wiring
    // =====================================================================================

    private void WireEvents()
    {
        // The video surface: click = play/pause, double-click = fullscreen,
        // press & hold on the right half (or hold the right mouse button) = 2x speed.
        Surface.MouseLeftButtonDown += OnSurfaceLeftDown;
        Surface.MouseLeftButtonUp += OnSurfaceLeftUp;
        Surface.MouseRightButtonDown += OnSurfaceRightDown;
        Surface.MouseRightButtonUp += OnSurfaceRightUp;
        Surface.LostMouseCapture += (_, _) =>
        {
            _holdTimer.Stop();
            _leftDown = false;
            _rightDown = false;
            if (_holding) EndHold();
        };

        Root.PreviewMouseMove += OnRootMouseMove;
        Root.MouseLeave += (_, _) =>
        {
            _lastMouseScreen = new Point(double.NaN, double.NaN);
            if (_hasMedia && CanAutoHide)
            {
                _hideTimer.Stop();
                _hideTimer.Interval = TimeSpan.FromMilliseconds(600);
                _hideTimer.Start();
            }
        };
        Root.PreviewMouseDown += (_, e) =>
        {
            if (_settingsOpen && !IsWithin(e.OriginalSource, SettingsPanel) && !IsWithin(e.OriginalSource, SettingsButton)
                && !IsWithin(e.OriginalSource, Surface))
                CloseSettings();
            if (!IsWithin(e.OriginalSource, Surface)) _host.ActivateHost();
        };
        Root.MouseWheel += OnRootMouseWheel;

        Root.DragEnter += OnDragOver;
        Root.DragOver += OnDragOver;
        Root.DragLeave += (_, e) =>
        {
            var p = e.GetPosition(Root);
            if (p.X <= 1 || p.Y <= 1 || p.X >= Root.ActualWidth - 1 || p.Y >= Root.ActualHeight - 1)
                DropHint.Visibility = Visibility.Collapsed;
        };
        Root.Drop += OnDrop;

        // Progress bar
        ProgressArea.MouseEnter += (_, _) => SetProgressHover(true);
        ProgressArea.MouseLeave += (_, _) => { if (!_seekDragging) SetProgressHover(false); };
        ProgressArea.MouseMove += (_, e) =>
        {
            var x = e.GetPosition(TrackGrid).X;
            UpdateProgressHover(x);
            if (_seekDragging) DragSeek(x);
        };
        ProgressArea.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (!_hasMedia || _lastLength <= 0) return;
            _seekDragging = true;
            ProgressArea.CaptureMouse();
            DragSeek(e.GetPosition(TrackGrid).X);
        };
        ProgressArea.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!_seekDragging) return;
            _seekDragging = false;
            ProgressArea.ReleaseMouseCapture();
            SeekTo(_dragMs);
            if (!ProgressArea.IsMouseOver) SetProgressHover(false);
            RestartHideTimer();
        };
        ProgressArea.LostMouseCapture += (_, _) =>
        {
            if (!_seekDragging) return;
            _seekDragging = false;
            SeekTo(_dragMs);
            SetProgressHover(false);
        };

        // Volume
        VolumeGroup.MouseEnter += (_, _) => ExpandVolume(true);
        VolumeGroup.MouseLeave += (_, _) => { if (!_volumeDragging) ExpandVolume(false); };
        VolumeButton.Click += (_, _) => ToggleMute();
        VolumeSlider.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _volumeDragging = true;
            VolumeSlider.CaptureMouse();
            SetVolumeFromSlider(e.GetPosition(VolumeSlider).X);
        };
        VolumeSlider.MouseMove += (_, e) => { if (_volumeDragging) SetVolumeFromSlider(e.GetPosition(VolumeSlider).X); };
        VolumeSlider.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _volumeDragging = false;
            VolumeSlider.ReleaseMouseCapture();
            if (!VolumeGroup.IsMouseOver) ExpandVolume(false);
        };
        VolumeSlider.LostMouseCapture += (_, _) => _volumeDragging = false;

        // Buttons
        PlayButton.Click += (_, _) => TogglePlay(false);
        PrevButton.Click += (_, _) => PlayPrevious();
        NextButton.Click += (_, _) => PlayNext();
        AutoplayButton.Click += (_, _) =>
        {
            _settings.Autoplay = !_settings.Autoplay;
            ApplyAutoplayVisual();
            RefreshTip();
        };
        CcButton.Click += (_, _) => ToggleSubtitles();
        TimePill.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _settings.ShowRemainingTime = !_settings.ShowRemainingTime;
            RefreshTimeline();
            RefreshTip();
        };
        SettingsButton.Click += (_, _) => { if (_settingsOpen) CloseSettings(); else OpenSettings(); };
        MiniButton.Click += (_, _) => _host.ToggleMiniPlayer();
        FullscreenButton.Click += (_, _) => _host.ToggleFullscreen();

        WelcomeOpenFile.Click += (_, _) => ShowOpenFileDialog();
        WelcomeOpenFolder.Click += (_, _) => ShowOpenFolderDialog();
        WelcomeOpenUrl.Click += (_, _) => ShowOpenUrlDialog();
        WelcomePanel.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowContextMenu(); };

        ShortcutsClose.Click += (_, _) => ShowShortcuts(false);
        ShortcutsPanel.MouseLeftButtonDown += (_, e) =>
        {
            if (!IsWithin(e.OriginalSource, ShortcutsCard)) ShowShortcuts(false);
        };

        // YouTube-style hover labels
        AttachTip(PlayButton, () => _ended ? "Replay" : (_engine.IsPlaying || _engine.IsLoading) ? "Pause (k)" : "Play (k)");
        AttachTip(PrevButton, () => "Previous (SHIFT+P)");
        AttachTip(NextButton, () => "Next (SHIFT+N)");
        AttachTip(VolumeButton, () => _settings.Muted ? "Unmute (m)" : "Mute (m)");
        AttachTip(AutoplayButton, () => _settings.Autoplay ? "Autoplay is on" : "Autoplay is off");
        AttachTip(CcButton, () => "Subtitles/closed captions (c)");
        AttachTip(TimePill, () => _settings.ShowRemainingTime ? "Show elapsed time" : "Show remaining time");
        AttachTip(SettingsButton, () => "Settings");
        AttachTip(MiniButton, () => _host.IsMiniPlayer ? "Expand (i)" : "Miniplayer (i)");
        AttachTip(FullscreenButton, () => _host.IsFullscreen ? "Exit full screen (f)" : "Full screen (f)");
    }

    private void OnSurfaceLeftDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _host.ActivateHost();

        if (_settingsOpen)
        {
            CloseSettings();
            return;
        }
        if (!_hasMedia) return;

        if (e.ClickCount == 2)
        {
            _holdTimer.Stop();
            _host.ToggleFullscreen();
            return;
        }

        _leftDown = true;
        Surface.CaptureMouse();
        if (e.GetPosition(Surface).X >= Surface.ActualWidth / 2) _holdTimer.Start();
    }

    private void OnSurfaceLeftUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _holdTimer.Stop();
        var wasHolding = _holding;
        _leftDown = false;

        if (wasHolding) EndHold();
        if (Surface.IsMouseCaptured && !_rightDown) Surface.ReleaseMouseCapture();
        // A plain click on the video intentionally does nothing (no pause/resume) - use Space or the play button.
    }

    private void OnSurfaceRightDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _host.ActivateHost();
        CloseSettings();
        _rightDown = true;
        Surface.CaptureMouse();
        if (_hasMedia) _holdTimer.Start();
    }

    private void OnSurfaceRightUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _holdTimer.Stop();
        var wasDown = _rightDown;
        var wasHolding = _holding;
        _rightDown = false;

        if (wasHolding) EndHold();
        if (Surface.IsMouseCaptured && !_leftDown) Surface.ReleaseMouseCapture();
        if (wasHolding || !wasDown) return;
        ShowContextMenu();
    }

    private void OnRootMouseMove(object sender, MouseEventArgs e)
    {
        // WPF also raises MouseMove when the layout under a still cursor changes - only react to real movement.
        Point screen;
        try { screen = Root.PointToScreen(e.GetPosition(Root)); }
        catch (InvalidOperationException) { return; }

        if (!double.IsNaN(_lastMouseScreen.X) && Math.Abs(screen.X - _lastMouseScreen.X) < 1 && Math.Abs(screen.Y - _lastMouseScreen.Y) < 1)
            return;
        _lastMouseScreen = screen;
        if (_hasMedia) ShowControls();
    }

    private void OnRootMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_hasMedia || _settingsOpen || ShortcutsPanel.Visibility == Visibility.Visible) return;
        e.Handled = true;

        // Ctrl + wheel: brightness.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SetBrightness(_settings.Brightness + (e.Delta > 0 ? 5 : -5), true);
            return;
        }

        // Over the progress bar or the time: seek (like VLC). Anywhere else: volume.
        var seek = IsWithin(e.OriginalSource, ProgressArea) || IsWithin(e.OriginalSource, TimePill);
        if (seek != _wheelSeeks) _wheelAccumulator = 0;
        _wheelSeeks = seek;

        // Touchpads send many small deltas - act once per mouse-wheel notch (120).
        _wheelAccumulator += e.Delta;
        while (Math.Abs(_wheelAccumulator) >= 120)
        {
            var up = _wheelAccumulator > 0;
            _wheelAccumulator -= up ? 120 : -120;
            if (seek) SeekBy(up ? 5_000 : -5_000);
            else SetVolume(_settings.Volume + (up ? 5 : -5), true);
        }
        ShowControls();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop) ||
                 (e.Data.GetDataPresent(DataFormats.UnicodeText) && MediaFormats.LooksLikeUrl(e.Data.GetData(DataFormats.UnicodeText) as string ?? ""));
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (ok) DropHint.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        e.Handled = true;
        _host.ActivateHost();
        _externalBatch.Clear();

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            OpenInputs(files);
        else if (e.Data.GetData(DataFormats.UnicodeText) is string text && MediaFormats.LooksLikeUrl(text))
            OpenInputs(new[] { text.Trim() });
    }

    // ---- progress bar interaction ---------------------------------------------------------

    private void SetProgressHover(bool on)
    {
        TrackGrid.Height = on ? 5 : 3;
        HoverFill.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        HoverTime.Visibility = on && _lastLength > 0 ? Visibility.Visible : Visibility.Collapsed;
        var scale = on ? 1.15 : 0.9;
        ThumbScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, TimeSpan.FromMilliseconds(100)));
        ThumbScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, TimeSpan.FromMilliseconds(100)));
    }

    private void UpdateProgressHover(double x)
    {
        var width = TrackGrid.ActualWidth;
        if (width <= 0 || _lastLength <= 0) return;
        x = MathEx.Clamp(x, 0, width);
        HoverFill.Width = x;

        HoverTimeText.Text = FormatTime((long)(_lastLength * (x / width)), _lastLength);
        HoverTime.Visibility = Visibility.Visible;
        HoverTime.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = HoverTime.DesiredSize;
        var at = TrackGrid.TranslatePoint(new Point(x, 0), TipLayer);
        var top = ProgressArea.TranslatePoint(new Point(0, 0), TipLayer).Y;
        Canvas.SetLeft(HoverTime, MathEx.Clamp(at.X - size.Width / 2, 6, Math.Max(6, TipLayer.ActualWidth - size.Width - 6)));
        Canvas.SetTop(HoverTime, top - size.Height - 4);
    }

    private void DragSeek(double x)
    {
        var width = TrackGrid.ActualWidth;
        if (width <= 0 || _lastLength <= 0) return;
        _dragMs = (long)(_lastLength * MathEx.Clamp(x / width, 0, 1));
        _shownTime = _dragMs;
        RefreshTimeline();

        // Live scrubbing, throttled so the decoder keeps up.
        if (!_ended && _engine.State is VLCState.Playing or VLCState.Paused &&
            (DateTime.UtcNow - _lastDragSeek).TotalMilliseconds > 120)
        {
            _lastDragSeek = DateTime.UtcNow;
            _engine.Time = _dragMs;
        }
    }

    private void ExpandVolume(bool open)
    {
        var target = open ? VolumeSlider.Width + VolumeSlider.Margin.Right : 0;
        VolumeSliderHost.BeginAnimation(WidthProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private void SetVolumeFromSlider(double x)
    {
        var usable = VolumeSlider.Width - VolumeThumb.Width;
        var fraction = MathEx.Clamp((x - VolumeThumb.Width / 2) / usable, 0, 1);
        SetVolume((int)Math.Round(fraction * 100), false);
    }

    private void ApplyAutoplayVisual()
    {
        var on = _settings.Autoplay;
        AutoplayKnobShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(on ? 16 : 0, TimeSpan.FromMilliseconds(150)));
        AutoplayKnobIcon.Data = Icon(on ? "Icon.Play" : "Icon.Pause");
        AutoplayTrack.Background = new SolidColorBrush(on ? Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        AutoplayKnob.Fill = new SolidColorBrush(on ? Colors.White : Color.FromRgb(0xCF, 0xCF, 0xCF));
    }

    // =====================================================================================
    //  Controls visibility & transient indicators
    // =====================================================================================

    private bool CanAutoHide =>
        _hasMedia && !_ended && _engine.IsPlaying && !_settingsOpen && !_seekDragging && !_volumeDragging &&
        !ControlsHost.IsMouseOver && !UpdateCard.IsMouseOver && ShortcutsPanel.Visibility != Visibility.Visible;

    private void ShowControls()
    {
        if (!_controlsVisible)
        {
            _controlsVisible = true;
            Chrome.IsHitTestVisible = true;
            Fade(Chrome, 1, 120);
            Root.Cursor = null;
            RefreshTimeline();
        }
        _hideTimer.Interval = TimeSpan.FromMilliseconds(2600);
        RestartHideTimer();
    }

    private void RestartHideTimer()
    {
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void HideControls()
    {
        if (!_controlsVisible || !CanAutoHide) return;
        _controlsVisible = false;
        Chrome.IsHitTestVisible = false;
        Fade(Chrome, 0, 300);
        HideTip();
        HoverTime.Visibility = Visibility.Collapsed;
        if (Root.IsMouseOver) Root.Cursor = Cursors.None;
    }

    private void Flash(string iconKey)
    {
        CenterFlashIcon.Data = Icon(iconKey);
        var duration = TimeSpan.FromMilliseconds(550);
        CenterFlash.BeginAnimation(OpacityProperty, new DoubleAnimation(0.95, 0, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
        var grow = new DoubleAnimation(1, 1.55, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        CenterFlashScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        CenterFlashScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void ShowInfo(string? iconKey, string text)
    {
        if (_holding) return;
        InfoIcon.Visibility = iconKey == null ? Visibility.Collapsed : Visibility.Visible;
        if (iconKey != null) InfoIcon.Data = Icon(iconKey);
        InfoText.Text = text;
        InfoChevrons.Visibility = Visibility.Collapsed;
        InfoPill.BeginAnimation(OpacityProperty, null);
        InfoPill.Opacity = 1;
        _infoTimer.Stop();
        _infoTimer.Start();
    }

    private void ShowToast(string text, double seconds = 3.5)
    {
        ToastText.Text = text;
        Toast.BeginAnimation(OpacityProperty, null);
        Toast.Opacity = 1;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        _toastTimer.Start();
    }

    private void ShowSeekBubble(long deltaMs)
    {
        var direction = Math.Sign(deltaMs);
        var seconds = (int)Math.Abs(deltaMs / 1000);
        if (direction == _seekBubbleDirection && _seekBubbleTimer.IsEnabled) _seekBubbleSeconds += seconds;
        else _seekBubbleSeconds = seconds;
        _seekBubbleDirection = direction;

        var bubble = direction < 0 ? SeekBubbleLeft : SeekBubbleRight;
        var other = direction < 0 ? SeekBubbleRight : SeekBubbleLeft;
        (direction < 0 ? SeekTextLeft : SeekTextRight).Text = _seekBubbleSeconds >= 60 && _seekBubbleSeconds % 60 == 0
            ? $"{_seekBubbleSeconds / 60} min"
            : $"{_seekBubbleSeconds} seconds";
        other.BeginAnimation(OpacityProperty, null);
        other.Opacity = 0;
        bubble.BeginAnimation(OpacityProperty, null);
        bubble.Opacity = 1;
        _seekBubbleTimer.Stop();
        _seekBubbleTimer.Start();
    }

    private static void Fade(UIElement element, double to, int ms) =>
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)));

    // ---- custom tooltips ------------------------------------------------------------------

    private void AttachTip(FrameworkElement element, Func<string> text)
    {
        _tips[element] = text;
        element.MouseEnter += (_, _) => ShowTip(element);
        element.MouseLeave += (_, _) => { if (_tipOwner == element) HideTip(); };
    }

    private void ShowTip(FrameworkElement element)
    {
        if (_settingsOpen || !_tips.TryGetValue(element, out var text)) return;
        _tipOwner = element;
        TipText.Text = text();
        TipBox.Visibility = Visibility.Visible;
        TipBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = TipBox.DesiredSize;
        var center = element.TranslatePoint(new Point(element.ActualWidth / 2, 0), TipLayer);
        var top = ProgressArea.TranslatePoint(new Point(0, 0), TipLayer).Y;
        Canvas.SetLeft(TipBox, MathEx.Clamp(center.X - size.Width / 2, 6, Math.Max(6, TipLayer.ActualWidth - size.Width - 6)));
        Canvas.SetTop(TipBox, top - size.Height - 6);
    }

    private void RefreshTip()
    {
        if (_tipOwner != null && TipBox.Visibility == Visibility.Visible && _tipOwner.IsMouseOver) ShowTip(_tipOwner);
    }

    private void HideTip()
    {
        _tipOwner = null;
        TipBox.Visibility = Visibility.Collapsed;
    }

    // =====================================================================================
    //  Settings menu (gear)
    // =====================================================================================

    private void OpenSettings()
    {
        _settingsOpen = true;
        HideTip();
        BuildSettingsMain();
        PositionSettings();
        SettingsPanel.Visibility = Visibility.Visible;
        SettingsPanel.Opacity = 0;
        Fade(SettingsPanel, 1, 120);
        SettingsRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(30, TimeSpan.FromMilliseconds(150)));
        ShowControls();
    }

    private void CloseSettings()
    {
        if (!_settingsOpen) return;
        _settingsOpen = false;
        SettingsPanel.Visibility = Visibility.Collapsed;
        SettingsRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
        RestartHideTimer();
    }

    private void PositionSettings()
    {
        var top = ControlsHost.TranslatePoint(new Point(0, 0), Root).Y;
        SettingsPanel.Margin = new Thickness(0, 0, 12, Math.Max(8, Root.ActualHeight - top + 6));
        SettingsScroll.MaxHeight = Math.Max(140, top - 20);
    }

    private void BuildSettingsMain()
    {
        SettingsItems.Children.Clear();

        SettingsItems.Children.Add(MenuRow("Icon.Loop", "Loop", null, false, () =>
        {
            _settings.Loop = !_settings.Loop;
            BuildSettingsMain();
        }, toggle: _settings.Loop));

        SettingsItems.Children.Add(MenuRow("Icon.Speed", "Playback speed", SpeedLabel(_rate), true, BuildSpeedMenu));

        var subTracks = _hasMedia ? _engine.SubtitleTracks : Array.Empty<TrackInfo>();
        var subId = _hasMedia ? _engine.Subtitle : -1;
        var subLabel = subId < 0 ? "Off" : subTracks.FirstOrDefault(t => t.Id == subId).Name ?? "On";
        SettingsItems.Children.Add(MenuRow("Icon.Subtitles", $"Subtitles/CC ({subTracks.Count})", subLabel, true, BuildSubtitleMenu));

        var audioTracks = _hasMedia ? _engine.AudioTracks : Array.Empty<TrackInfo>();
        var audioLabel = audioTracks.FirstOrDefault(t => t.Id == _engine.AudioTrack).Name ?? "Default";
        SettingsItems.Children.Add(MenuRow("Icon.Audio", "Audio track", audioLabel, true, BuildAudioMenu));

        var aspect = AspectRatios.FirstOrDefault(a => a.Value == _engine.AspectRatio).Label ?? "Default";
        SettingsItems.Children.Add(MenuRow("Icon.AspectRatio", "Aspect ratio", aspect, true, BuildAspectMenu));

        SettingsItems.Children.Add(MenuRow("Icon.Brightness", "Brightness", $"{_settings.Brightness}%", true, BuildBrightnessMenu));

        var skipValue = !_settings.SkipEnabled ? "Off" : _skips.Count == 0 ? "None" : $"{_skips.Count}";
        SettingsItems.Children.Add(MenuRow("Icon.FastForward", "Skip scenes", skipValue, true, BuildSkipMenu));

        SettingsItems.Children.Add(MenuRow("Icon.Ai", "AI scene filter", AiMenuValue(), true, BuildAiMenu));

        SettingsItems.Children.Add(MenuRow("Icon.Camera", "Take snapshot", null, false, () =>
        {
            CloseSettings();
            TakeSnapshot();
        }));

        if (AppInfo.UpdatesEnabled)
            SettingsItems.Children.Add(MenuRow("Icon.Download", "Updates", UpdatesMenuValue(), true, BuildUpdatesMenu));
    }

    private void BuildSpeedMenu() =>
        BuildSubMenu("Playback speed", Speeds.Select(s => (SpeedLabel(s), Math.Abs(s - _rate) < 0.01f, (Action)(() =>
        {
            SetRate(s, false);
            BuildSettingsMain();
        }))));

    private void BuildSubtitleMenu()
    {
        var tracks = _engine.SubtitleTracks;
        var current = _engine.Subtitle;
        var options = new List<(string, bool, Action)>
        {
            ("Off", current < 0, () => { _engine.SetSubtitle(-1); UpdateCcState(); BuildSettingsMain(); }),
        };
        options.AddRange(tracks.Select(t => (t.Name, t.Id == current, (Action)(() =>
        {
            _engine.SetSubtitle(t.Id);
            _lastSubtitleId = t.Id;
            UpdateCcState();
            BuildSettingsMain();
        }))));
        BuildSubMenu("Subtitles/CC", options, ("Add subtitle file…", () =>
        {
            CloseSettings();
            ShowAddSubtitleDialog();
        }));
    }

    private void BuildAudioMenu()
    {
        var tracks = _engine.AudioTracks;
        var current = _engine.AudioTrack;
        BuildSubMenu("Audio track", tracks.Select(t => (t.Name, t.Id == current, (Action)(() =>
        {
            _engine.SetAudioTrack(t.Id);
            BuildSettingsMain();
        }))));
    }

    private void BuildAspectMenu()
    {
        var current = _engine.AspectRatio;
        BuildSubMenu("Aspect ratio", AspectRatios.Select(a => (a.Label, a.Value == current, (Action)(() =>
        {
            _engine.AspectRatio = a.Value;
            BuildSettingsMain();
        }))));
    }

    private void BuildSkipMenu()
    {
        SettingsItems.Children.Clear();
        SettingsItems.Children.Add(MenuRow("Icon.ChevronLeft", "Skip scenes", null, false, BuildSettingsMain, bold: true));
        SettingsItems.Children.Add(MenuSeparator());

        SettingsItems.Children.Add(MenuRow(null, "Skip marked parts", null, false, () =>
        {
            ToggleSkipping();
            BuildSkipMenu();
        }, toggle: _settings.SkipEnabled));

        SettingsItems.Children.Add(MenuRow(null, "Show “Skipped” message", null, false, () =>
        {
            _settings.ShowSkipNotice = !_settings.ShowSkipNotice;
            BuildSkipMenu();
        }, toggle: _settings.ShowSkipNotice));

        foreach (var range in _skips.ToList())
        {
            SettingsItems.Children.Add(MenuRow("Icon.FastForward",
                $"{FormatTime(range.StartMs, _lastLength)} – {FormatTime(range.EndMs, _lastLength)}", "Remove", false, () =>
                {
                    RemoveSkip(range);
                    BuildSkipMenu();
                }));
        }

        SettingsItems.Children.Add(MenuSeparator());
        SettingsItems.Children.Add(MenuRow(null, "Scenes to skip…", ScenesMenuValue(), false, () => ShowScenesDialog()));
        SettingsItems.Children.Add(MenuRow(null, "Add a skip (type the times)…", null, false, () =>
        {
            CloseSettings();
            ShowAddSkipDialog();
        }));
        SettingsItems.Children.Add(MenuRow(null, _pendingSkipStart is { } s
                ? $"End skip here ({FormatTime(s, _lastLength)} → now)   ]"
                : "Start skip here   [", null, false, () =>
        {
            CloseSettings();
            if (_pendingSkipStart == null) MarkSkipStart();
            else MarkSkipEnd();
        }));
        PositionSettings();
    }

    private void BuildBrightnessMenu()
    {
        SettingsItems.Children.Clear();
        SettingsItems.Children.Add(MenuRow("Icon.ChevronLeft", "Brightness", null, false, BuildSettingsMain, bold: true));
        SettingsItems.Children.Add(MenuSeparator());

        var value = new TextBlock
        {
            Text = $"{_settings.Brightness}%",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        };
        SettingsItems.Children.Add(value);
        SettingsItems.Children.Add(MakeSlider(0, 200, () => _settings.Brightness, v =>
        {
            SetBrightness((int)Math.Round(v / 5) * 5, false);
            value.Text = $"{_settings.Brightness}%";
        }));
        SettingsItems.Children.Add(MenuRow(null, "Reset to 100%", null, false, () =>
        {
            SetBrightness(100, false);
            BuildBrightnessMenu();
        }));
        SettingsItems.Children.Add(new TextBlock
        {
            Text = "Tip: Ctrl + ↑/↓ or Ctrl + mouse wheel",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)),
            Margin = new Thickness(18, 4, 18, 8),
        });
        PositionSettings();
    }

    /// <summary>A YouTube-style horizontal slider for the settings menu.</summary>
    private static FrameworkElement MakeSlider(double min, double max, Func<double> get, Action<double> set)
    {
        var grid = new Grid { Height = 40, Margin = new Thickness(18, 0, 18, 2), Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), Cursor = Cursors.Hand };
        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center };
        var fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = (Brush)Application.Current.FindResource("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        var thumb = new System.Windows.Shapes.Ellipse { Width = 14, Height = 14, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(track);
        grid.Children.Add(fill);
        grid.Children.Add(thumb);

        void Layout()
        {
            var usable = Math.Max(0, grid.ActualWidth - thumb.Width);
            var fraction = MathEx.Clamp((get() - min) / (max - min), 0, 1);
            thumb.Margin = new Thickness(fraction * usable, 0, 0, 0);
            fill.Width = fraction * usable + thumb.Width / 2;
        }

        void SetFromX(double x)
        {
            var usable = Math.Max(1, grid.ActualWidth - thumb.Width);
            set(min + MathEx.Clamp((x - thumb.Width / 2) / usable, 0, 1) * (max - min));
            Layout();
        }

        grid.SizeChanged += (_, _) => Layout();
        grid.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            grid.CaptureMouse();
            SetFromX(e.GetPosition(grid).X);
        };
        grid.MouseMove += (_, e) => { if (grid.IsMouseCaptured) SetFromX(e.GetPosition(grid).X); };
        grid.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            grid.ReleaseMouseCapture();
        };
        return grid;
    }

    private void SetBrightness(int percent, bool showInfo)
    {
        _settings.Brightness = MathEx.Clamp(percent, 0, 200);
        _engine.SetBrightness(_settings.Brightness / 100f);
        if (showInfo) ShowInfo("Icon.Brightness", $"Brightness {_settings.Brightness}%");
    }

    private static Border MenuSeparator() =>
        new() { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 4, 0, 6) };

    private void BuildSubMenu(string title, IEnumerable<(string Label, bool Selected, Action OnClick)> options, (string Label, Action OnClick)? footer = null)
    {
        SettingsItems.Children.Clear();

        var header = MenuRow("Icon.ChevronLeft", title, null, false, BuildSettingsMain, bold: true);
        SettingsItems.Children.Add(header);
        SettingsItems.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 4, 0, 6) });

        var any = false;
        foreach (var (label, selected, onClick) in options)
        {
            any = true;
            SettingsItems.Children.Add(MenuRow(selected ? "Icon.Check" : null, label, null, false, onClick));
        }
        if (!any)
            SettingsItems.Children.Add(new TextBlock { Text = "Nothing available", Foreground = Brushes.Gray, Margin = new Thickness(52, 8, 16, 10) });

        if (footer is { } f)
        {
            SettingsItems.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 6, 0, 4) });
            SettingsItems.Children.Add(MenuRow("Icon.File", f.Label, null, false, f.OnClick));
        }
        PositionSettings();
    }

    private FrameworkElement MenuRow(string? iconKey, string label, string? value, bool chevron, Action onClick, bool? toggle = null, bool bold = false)
    {
        var grid = new Grid { MinHeight = 40 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (iconKey != null)
            grid.Children.Add(new IconView { Data = Icon(iconKey), Width = 22, Height = 22, Margin = new Thickness(14, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left });

        var text = new TextBlock
        {
            Text = label,
            FontSize = 14,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 12, 0),
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        if (value != null)
            right.Children.Add(new TextBlock { Text = value, FontSize = 13.5, Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis });
        if (chevron)
            right.Children.Add(new IconView { Data = Icon("Icon.ChevronRight"), Width = 20, Height = 20, Margin = new Thickness(4, 0, 0, 0) });
        if (toggle is { } on)
            right.Children.Add(MakeSwitch(on));
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        var row = new ActionBorder { Child = grid, Background = Brushes.Transparent, Cursor = Cursors.Hand, Padding = new Thickness(0, 0, 4, 0), Invoked = onClick };
        System.Windows.Automation.AutomationProperties.SetName(row, label);
        row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return row;
    }

    private static FrameworkElement MakeSwitch(bool on)
    {
        var grid = new Grid { Width = 36, Height = 20, Margin = new Thickness(0, 0, 4, 0) };
        grid.Children.Add(new Border
        {
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(on ? Color.FromRgb(0xFF, 0x00, 0x33) : Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        grid.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 20,
            Height = 20,
            Fill = Brushes.White,
            HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        });
        return grid;
    }

    private static string SpeedLabel(float rate) => Math.Abs(rate - 1f) < 0.01f ? "Normal" : rate.ToString("0.##");

    // =====================================================================================
    //  Right-click menu (VLC-style)
    // =====================================================================================

    private void ShowContextMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Open file…", "Ctrl+O", ShowOpenFileDialog));
        menu.Items.Add(MenuItem("Open folder…", "Ctrl+F", ShowOpenFolderDialog));
        menu.Items.Add(MenuItem("Open network stream…", "Ctrl+N", ShowOpenUrlDialog));

        var recent = new MenuItem { Header = "Recent" };
        foreach (var r in _settings.Recent.Take(10))
        {
            var source = r;
            recent.Items.Add(MenuItem(Shorten(source), null, () => { _externalBatch.Clear(); OpenInputs(new[] { source }); }));
        }
        if (recent.Items.Count > 0)
        {
            recent.Items.Add(new Separator());
            recent.Items.Add(MenuItem("Clear recent", null, () => { _settings.Recent.Clear(); RefreshRecent(); }));
        }
        else recent.IsEnabled = false;
        menu.Items.Add(recent);

        if (_hasMedia)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem(_engine.IsPlaying ? "Pause" : "Play", "Space", () => TogglePlay(false)));
            if (_playlist.HasPrevious) menu.Items.Add(MenuItem("Previous", "Shift+P", PlayPrevious));
            if (_playlist.HasNext) menu.Items.Add(MenuItem("Next", "Shift+N", PlayNext));

            var speed = new MenuItem { Header = "Playback speed" };
            foreach (var s in Speeds)
            {
                var value = s;
                speed.Items.Add(MenuItem(SpeedLabel(s), null, () => SetRate(value, true), Math.Abs(s - _rate) < 0.01f));
            }
            menu.Items.Add(speed);

            var audio = new MenuItem { Header = "Audio track" };
            foreach (var t in _engine.AudioTracks)
            {
                var id = t.Id;
                audio.Items.Add(MenuItem(t.Name, null, () => _engine.SetAudioTrack(id), id == _engine.AudioTrack));
            }
            audio.IsEnabled = audio.Items.Count > 0;
            menu.Items.Add(audio);

            var subs = new MenuItem { Header = "Subtitles" };
            subs.Items.Add(MenuItem("Off", null, () => { _engine.SetSubtitle(-1); UpdateCcState(); }, _engine.Subtitle < 0));
            foreach (var t in _engine.SubtitleTracks)
            {
                var id = t.Id;
                subs.Items.Add(MenuItem(t.Name, null, () => { _engine.SetSubtitle(id); UpdateCcState(); }, id == _engine.Subtitle));
            }
            subs.Items.Add(new Separator());
            subs.Items.Add(MenuItem("Add subtitle file…", null, ShowAddSubtitleDialog));
            menu.Items.Add(subs);

            var aspect = new MenuItem { Header = "Aspect ratio" };
            foreach (var a in AspectRatios)
            {
                var value = a.Value;
                aspect.Items.Add(MenuItem(a.Label, null, () => _engine.AspectRatio = value, a.Value == _engine.AspectRatio));
            }
            menu.Items.Add(aspect);

            var skip = new MenuItem { Header = "Skip scenes" };
            skip.Items.Add(MenuItem("Scenes to skip…", null, () => ShowScenesDialog()));
            skip.Items.Add(MenuItem("Add a skip…", null, ShowAddSkipDialog));
            skip.Items.Add(_pendingSkipStart == null
                ? MenuItem("Start skip here", "[", MarkSkipStart)
                : MenuItem("End skip here", "]", MarkSkipEnd));
            if (_skips.Count > 0)
            {
                skip.Items.Add(new Separator());
                foreach (var range in _skips.ToList())
                    skip.Items.Add(MenuItem($"Remove {FormatTime(range.StartMs, _lastLength)} – {FormatTime(range.EndMs, _lastLength)}", null, () => RemoveSkip(range)));
            }
            skip.Items.Add(new Separator());
            skip.Items.Add(MenuItem("Skip marked parts", null, ToggleSkipping, _settings.SkipEnabled));
            skip.Items.Add(MenuItem("Show “Skipped” message", null, () => _settings.ShowSkipNotice = !_settings.ShowSkipNotice, _settings.ShowSkipNotice));
            menu.Items.Add(skip);
            menu.Items.Add(AiContextMenu());

            menu.Items.Add(MenuItem("Loop", null, () => _settings.Loop = !_settings.Loop, _settings.Loop));
            menu.Items.Add(MenuItem("Autoplay next file", null, () => { _settings.Autoplay = !_settings.Autoplay; ApplyAutoplayVisual(); }, _settings.Autoplay));
            menu.Items.Add(MenuItem("Take snapshot", "Shift+S", TakeSnapshot));
            menu.Items.Add(MenuItem("Close file", "Ctrl+W", CloseMedia));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(_host.IsFullscreen ? "Exit full screen" : "Full screen", "F", _host.ToggleFullscreen));
        menu.Items.Add(MenuItem("Miniplayer", "I", _host.ToggleMiniPlayer, _host.IsMiniPlayer));
        menu.Items.Add(MenuItem("Always on top", "T", () => _host.AlwaysOnTop = !_host.AlwaysOnTop, _host.AlwaysOnTop));
        var windows = new MenuItem { Header = $"If {AppInfo.Name} is already open" };
        windows.Items.Add(MenuItem("Ask every time", null, () => _settings.InstanceMode = InstanceMode.Ask, _settings.InstanceMode == InstanceMode.Ask));
        windows.Items.Add(MenuItem("Play in the open window", null, () => _settings.InstanceMode = InstanceMode.UseExisting, _settings.InstanceMode == InstanceMode.UseExisting));
        windows.Items.Add(MenuItem("Always open a new window", null, () => _settings.InstanceMode = InstanceMode.NewWindow, _settings.InstanceMode == InstanceMode.NewWindow));
        menu.Items.Add(windows);
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Keyboard shortcuts & about", "?", () => ShowShortcuts(true)));
        if (AppInfo.UpdatesEnabled) menu.Items.Add(MenuItem("Check for updates…", null, () => _ = CheckForUpdatesAsync(manual: true)));
        menu.Items.Add(MenuItem("Exit", "Ctrl+Q", () => _host.HostWindow.Close()));

        menu.Placement = PlacementMode.MousePoint;
        menu.PlacementTarget = this;
        menu.IsOpen = true;
    }

    private static MenuItem MenuItem(string header, string? gesture, Action onClick, bool isChecked = false)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsChecked = isChecked };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static string Shorten(string source)
    {
        var name = MediaFormats.LooksLikeUrl(source) ? source : Path.GetFileName(source.TrimEnd('\\'));
        if (string.IsNullOrEmpty(name)) name = source;
        name = name.Replace("_", "__");
        return name.Length > 60 ? name[..57] + "…" : name;
    }

    // =====================================================================================
    //  Start screen, shortcuts panel
    // =====================================================================================

    private void RefreshRecent()
    {
        RecentList.Children.Clear();
        var items = _settings.Recent
            .Where(r => MediaFormats.LooksLikeUrl(r) || File.Exists(r) || Directory.Exists(r))
            .Take(5)
            .ToList();
        RecentSection.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var source in items)
        {
            var isUrl = MediaFormats.LooksLikeUrl(source);
            var iconKey = isUrl ? "Icon.Link" : Directory.Exists(source) ? "Icon.Folder"
                : MediaFormats.KindOf(source) == MediaKind.Audio ? "Icon.Audio" : "Icon.Movie";

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var badge = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x27)),
                Child = new IconView { Data = Icon(iconKey), Width = 20, Height = 20 },
            };
            grid.Children.Add(badge);

            var texts = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock
            {
                Text = isUrl ? source : Path.GetFileName(source.TrimEnd('\\')),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var resume = isUrl ? 0 : _settings.GetResume(source);
            texts.Children.Add(new TextBlock
            {
                Text = isUrl ? "Network stream" : (Path.GetDirectoryName(source) ?? "") + (resume > 0 ? $"  ·  stopped at {FormatTime(resume, resume)}" : ""),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
            });
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);

            var row = new Border
            {
                Child = grid,
                Padding = new Thickness(12, 8, 12, 8),
                CornerRadius = new CornerRadius(12),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
            };
            row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x27));
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _externalBatch.Clear();
                OpenInputs(new[] { source });
            };
            RecentList.Children.Add(row);
        }
    }

    private void BuildShortcuts()
    {
        var shortcuts = new (string Keys, string What)[]
        {
            ("Space  K", "Play / pause"),
            ("[  ]", "Start / end a skipped part"),
            ("Wheel on bar", "Seek back / forward 5 seconds"),
            ("J  L", "Back / forward 10 seconds"),
            ("←  →", "Back / forward 5 seconds"),
            ("Ctrl+←  Ctrl+→", "Back / forward 1 minute"),
            ("↑  ↓", "Volume up / down"),
            ("M", "Mute / unmute"),
            ("F  Double-click", "Full screen"),
            ("Esc", "Exit full screen / miniplayer"),
            ("0 … 9", "Jump to 0% … 90%"),
            ("Home  End", "Start / end"),
            ("Shift+>  Shift+<", "Faster / slower"),
            (".  ,", "Next / previous frame (paused)"),
            ("Hold right side", "Play at 2x while held"),
            ("Hold right button", "Play at 2x while held"),
            ("C", "Subtitles on / off"),
            ("G  H", "Subtitles earlier / later (sync)"),
            ("V  B", "Cycle subtitle / audio track"),
            ("A", "Cycle aspect ratio"),
            ("I", "Miniplayer"),
            ("T", "Always on top"),
            ("Shift+N  Shift+P", "Next / previous file"),
            ("Shift+S", "Snapshot"),
            ("Wheel on video", "Volume"),
            ("Ctrl+↑  Ctrl+↓", "Brightness up / down (or Ctrl + wheel)"),
            ("Ctrl+O  Ctrl+F", "Open file / folder"),
            ("Ctrl+N", "Open network stream"),
        };

        foreach (var (keys, what) in shortcuts)
        {
            var row = new DockPanel { Margin = new Thickness(0, 5, 24, 5) };
            var chips = new WrapPanel { Width = 170 };
            foreach (var key in keys.Split("  "))
            {
                chips.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(7, 2, 7, 3),
                    Margin = new Thickness(0, 0, 6, 0),
                    Child = new TextBlock { Text = key, FontSize = 12, FontWeight = FontWeights.SemiBold },
                });
            }
            DockPanel.SetDock(chips, Dock.Left);
            row.Children.Add(chips);
            row.Children.Add(new TextBlock { Text = what, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            ShortcutsGrid.Children.Add(row);
        }
        AboutText.Text = AboutLine();
    }

    private string AboutLine()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        var engine = string.IsNullOrEmpty(_engine.VlcVersion) ? "LibVLC" : "LibVLC " + _engine.VlcVersion.Split(' ')[0];
        return $"{AppInfo.Name} {version}  ·  Playback engine: {engine} (the engine inside VLC media player, LGPL)  ·  AI scene filter: {AiAboutText()}  ·  Settings: %APPDATA%\\{AppInfo.Id}";
    }

    private void ShowShortcuts(bool show)
    {
        ShortcutsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            CloseSettings();
            AboutText.Text = AboutLine();
        }
        else RestartHideTimer();
    }

    // =====================================================================================
    //  Keyboard (YouTube + VLC shortcuts)
    // =====================================================================================

    public void HandleKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var ctrl = mods.HasFlag(ModifierKeys.Control);
        var shift = mods.HasFlag(ModifierKeys.Shift);
        var alt = mods.HasFlag(ModifierKeys.Alt);

        if (ShortcutsPanel.Visibility == Visibility.Visible)
        {
            ShowShortcuts(false);
            e.Handled = true;
            return;
        }

        var handled = true;
        if (ctrl)
        {
            switch (key)
            {
                case Key.O: ShowOpenFileDialog(); break;
                case Key.F: ShowOpenFolderDialog(); break;
                case Key.N:
                case Key.U: ShowOpenUrlDialog(); break;
                case Key.W: CloseMedia(); break;
                case Key.Q: _host.HostWindow.Close(); break;
                case Key.Left: SeekBy(-60_000); break;
                case Key.Right: SeekBy(60_000); break;
                case Key.Up: SetBrightness(_settings.Brightness + 10, true); break;
                case Key.Down: SetBrightness(_settings.Brightness - 10, true); break;
                default: handled = false; break;
            }
            e.Handled = handled;
            return;
        }

        if (alt && key == Key.Enter)
        {
            _host.ToggleFullscreen();
            e.Handled = true;
            return;
        }
        if (alt)
            return; // let Alt+F4 and the system menu work

        switch (key)
        {
            case Key.F:
            case Key.F11:
                _host.ToggleFullscreen();
                break;
            case Key.Escape:
                if (_settingsOpen) CloseSettings();
                else _host.LeaveSpecialModes();
                break;
            case Key.OemQuestion when shift:
                ShowShortcuts(true);
                break;
            case Key.I:
                _host.ToggleMiniPlayer();
                break;
            case Key.T:
                _host.AlwaysOnTop = !_host.AlwaysOnTop;
                ShowInfo(null, _host.AlwaysOnTop ? "Always on top: on" : "Always on top: off");
                break;
            default:
                handled = _hasMedia && HandleMediaKey(key, shift);
                break;
        }
        e.Handled = handled;
    }

    private bool HandleMediaKey(Key key, bool shift)
    {
        switch (key)
        {
            case Key.Space:
            case Key.K:
            case Key.MediaPlayPause:
                TogglePlay(true);
                break;
            case Key.MediaStop:
                CloseMedia();
                break;
            case Key.J: SeekBy(-10_000); break;
            case Key.L: SeekBy(10_000); break;
            case Key.Left: SeekBy(-5_000); break;
            case Key.Right: SeekBy(5_000); break;
            case Key.Up: SetVolume(_settings.Volume + 5, true); break;
            case Key.Down: SetVolume(_settings.Volume - 5, true); break;
            case Key.M: ToggleMute(); break;
            case Key.Home: SeekTo(0); break;
            case Key.End: SeekTo(_lastLength); break;
            case >= Key.D0 and <= Key.D9 when !shift: SeekToFraction((key - Key.D0) / 10.0); break;
            case >= Key.NumPad0 and <= Key.NumPad9: SeekToFraction((key - Key.NumPad0) / 10.0); break;
            case Key.OemPeriod when shift: SetRate(NextSpeed(+1), true); break;
            case Key.OemComma when shift: SetRate(NextSpeed(-1), true); break;
            case Key.OemPeriod:
                if (!_engine.IsPlaying) _engine.NextFrame();
                break;
            case Key.OemComma:
                if (!_engine.IsPlaying) SeekTo(Math.Max(0, _lastTime - 42));
                break;
            case Key.C: ToggleSubtitles(); break;
            case Key.OemOpenBrackets: MarkSkipStart(); break;
            case Key.OemCloseBrackets: MarkSkipEnd(); break;
            case Key.G: ShiftSubtitles(-100); break;
            case Key.H: ShiftSubtitles(100); break;
            case Key.V: CycleSubtitles(); break;
            case Key.B: CycleAudioTrack(); break;
            case Key.A: CycleAspectRatio(); break;
            case Key.N when shift:
            case Key.MediaNextTrack:
                PlayNext();
                break;
            case Key.P when shift:
            case Key.MediaPreviousTrack:
                PlayPrevious();
                break;
            case Key.S when shift: TakeSnapshot(); break;
            default: return false;
        }

        ShowControls();
        return true;
    }

    private float NextSpeed(int direction)
    {
        if (direction > 0) return Speeds.FirstOrDefault(s => s > _rate + 0.01f, Speeds[^1]);
        return Speeds.LastOrDefault(s => s < _rate - 0.01f, Speeds[0]);
    }

    // =====================================================================================
    //  Helpers
    // =====================================================================================

    private static Geometry Icon(string key) => (Geometry)Application.Current.FindResource(key);

    private static bool IsWithin(object? source, DependencyObject ancestor)
    {
        var node = source as DependencyObject;
        while (node != null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    public static string FormatTime(long ms, long reference)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return reference >= 3_600_000
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }
}

internal static class AnimationExtensions
{
    public static T Also<T>(this T value, Action<T> configure)
    {
        configure(value);
        return value;
    }
}
