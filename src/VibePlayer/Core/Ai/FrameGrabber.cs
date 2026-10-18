using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace VibePlayer.Core.Ai;

/// <summary>One decoded picture: 32-bit BGRX pixels, rows tightly packed.</summary>
public sealed class VideoFrame
{
    public VideoFrame(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    /// <summary>VLC timestamp in microseconds (on VLC's system clock).</summary>
    public long Pts { get; set; }
    /// <summary>Position in the video, in milliseconds.</summary>
    public long TimeMs { get; set; }
}

/// <summary>What the scanner learns from a file's first picture.</summary>
public sealed class VideoInfo
{
    public bool HasVideo { get; set; }
    public long LengthMs { get; set; }
    /// <summary>Picture shape (any scale): only the aspect ratio is used.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>
/// Reads a video's pictures for the AI, 2 per second of video, through LibVLC's stream output
/// (transcode to raw RGB, scaled down, then "smem" hands each picture to us with its exact timestamp).
/// There is no playback clock: it decodes as fast as the PC can, and simply waits while the AI is busy.
/// It has its own LibVLC instance (no audio, no window), separate from the player.
/// Picture timestamps are on VLC's system clock: only differences within one run are meaningful.
/// (Media.Parse is deliberately not used: parsing and disposing a Media here crashed the process later on,
/// so the size and length come from decoding the first picture instead.)
/// </summary>
public sealed class FrameGrabber : IDisposable
{
    /// <summary>The AI looks at one picture per this much video (2 per second).</summary>
    public const long SampleIntervalMs = 500;

    /// <summary>The shorter side of the pictures (the models use 224 and 320 pixels).</summary>
    private const int ShortSide = 320;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PrerenderCallback(IntPtr data, ref IntPtr buffer, UIntPtr size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PostrenderCallback(IntPtr data, IntPtr buffer, int width, int height, int bitsPerPixel, UIntPtr size, long pts);

    private static readonly object VlcGate = new();
    private static LibVLC? s_vlc;

    // Kept as fields: native code calls them, so the GC must not collect them.
    private readonly PrerenderCallback _prerender;
    private readonly PostrenderCallback _postrender;
    private IntPtr _buffer;
    private int _bufferSize;

    private readonly ConcurrentBag<VideoFrame> _pool = new();
    private Action<VideoFrame>? _onFrame;

    public FrameGrabber()
    {
        _prerender = OnPrerender;
        _postrender = OnPostrender;
    }

    private static LibVLC Vlc
    {
        get
        {
            lock (VlcGate)
            {
                if (s_vlc != null) return s_vlc;
                PlayerEngine.EnsureNativeLibrary();
                var options = new List<string>
                {
                    "--no-stats", "--no-audio", "--no-spu", "--no-osd", "--no-video-title-show",
                    "--no-sub-autodetect-file", "--no-sout-audio", "--no-sout-spu",
                    // Decoding is the slow part; the AI only needs small pictures, so trade a little sharpness for speed.
                    "--avcodec-skiploopfilter=4", "--avcodec-skip-frame=1", "--avcodec-fast",
                };
                // For diagnosis: VIBEVIDEOAI_VLC_LOG=<file> writes VLC's own log.
                var logFile = Environment.GetEnvironmentVariable("VIBEVIDEOAI_VLC_LOG");
                options.Add(string.IsNullOrEmpty(logFile) ? "--quiet" : "--verbose=2");
                s_vlc = new LibVLC(options.ToArray());
                if (!string.IsNullOrEmpty(logFile))
                {
                    var gate = new object();
                    s_vlc.Log += (_, e) =>
                    {
                        lock (gate)
                            File.AppendAllText(logFile!, $"{DateTime.Now:HH:mm:ss.fff} {e.Level} {e.Module}: {e.Message}{Environment.NewLine}");
                    };
                }
                return s_vlc;
            }
        }
    }

    /// <summary>
    /// Decodes the first picture (at a quarter size) to learn the picture shape and the length.
    /// <see cref="VideoInfo.HasVideo"/> is false for files without a picture.
    /// </summary>
    public async Task<VideoInfo> ReadInfoAsync(string path, CancellationToken ct)
    {
        var info = new VideoInfo();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long firstFrameMs = -1;
        using var first = CancellationTokenSource.CreateLinkedTokenSource(ct);
        first.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            await RunCoreAsync(path, "scale=0.25", 0, 0, frame =>
            {
                if (!info.HasVideo)
                {
                    info.HasVideo = true;
                    info.Width = frame.Width;
                    info.Height = frame.Height;
                    firstFrameMs = clock.ElapsedMilliseconds;
                }
                first.Cancel(); // that's all we need (the frame isn't pooled: it may be a different size)
            }, player => info.LengthMs = Math.Max(info.LengthMs, player.Length), first.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // stopped after the first picture (or gave up waiting for one)
        }
        Log.Info($"AI video info: {info.Width}x{info.Height}, {info.LengthMs} ms long; first picture after {firstFrameMs} ms, done after {clock.ElapsedMilliseconds} ms");
        return info;
    }

    /// <summary>
    /// Reads pictures from <paramref name="startMs"/> to <paramref name="stopMs"/> (0 = to the end) and calls
    /// <paramref name="onFrame"/> for each one, on VLC's thread. The callback may block (that pauses decoding);
    /// it must hand the frame back with <see cref="Recycle"/> when done. Completes at the end or when cancelled.
    /// </summary>
    public Task RunAsync(string path, VideoInfo info, long startMs, long stopMs, Action<VideoFrame> onFrame, CancellationToken ct)
    {
        var (width, height) = TargetSize(info);
        return RunCoreAsync(path, $"width={width},height={height}", startMs, stopMs, onFrame, null, ct);
    }

    /// <summary>For tests: sees every frame (with its computed time) before the scanner does.</summary>
    internal static Action<VideoFrame>? Observer { get; set; }

    private long _startMs;
    private long? _firstPts;
    private int _outputIndex;

    private async Task RunCoreAsync(string path, string size, long startMs, long stopMs, Action<VideoFrame> onFrame,
        Action<MediaPlayer>? beforeStop, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _onFrame = onFrame;
        _startMs = startMs;
        _firstPts = null;
        _outputIndex = 0;

        var pre = Marshal.GetFunctionPointerForDelegate(_prerender).ToInt64();
        var post = Marshal.GetFunctionPointerForDelegate(_postrender).ToInt64();
        // The fps filter drops the other pictures before they are converted and scaled (much faster at 1080p+).
        var fps = 1000 / SampleIntervalMs;
        var sout = $"#transcode{{vcodec=RV32,fps={fps},{size}}}" +
                   $":smem{{video-prerender-callback={pre},video-postrender-callback={post},video-data=0,no-time-sync}}";

        using var media = new Media(Vlc, path, FromType.FromPath);
        media.AddOption(":sout=" + sout);
        media.AddOption(":no-sout-audio");
        media.AddOption(":no-sout-spu");
        media.AddOption(":no-audio");
        media.AddOption(":no-spu");
        if (startMs > 0) media.AddOption(":start-time=" + Seconds(startMs));
        if (stopMs > 0) media.AddOption(":stop-time=" + Seconds(stopMs));

        using var player = new MediaPlayer(media);
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.EndReached += (_, _) => done.TrySetResult(true);
        player.Stopped += (_, _) => done.TrySetResult(true);
        player.EncounteredError += (_, _) => done.TrySetException(new IOException("VLC couldn't read this file for the AI scan."));

        using (ct.Register(() => done.TrySetCanceled()))
        {
            if (!player.Play()) throw new IOException("VLC couldn't start reading this file for the AI scan.");
            try
            {
                await done.Task.ConfigureAwait(false);
            }
            finally
            {
                // Never stop from VLC's own threads; the frame callback returns quickly once ct is cancelled.
                await Task.Run(() =>
                {
                    try { beforeStop?.Invoke(player); }
                    catch (Exception ex) { Log.Error(ex, "AI frame grabber"); }
                    player.Stop();
                }).ConfigureAwait(false);
                _onFrame = null;
            }
        }
    }

    /// <summary>Gives a frame back for reuse once the AI is done with it.</summary>
    public void Recycle(VideoFrame frame) => _pool.Add(frame);

    private static (int Width, int Height) TargetSize(VideoInfo info)
    {
        int w = info.Width, h = info.Height;
        if (w <= 0 || h <= 0) return (ShortSide * 16 / 9 / 2 * 2, ShortSide);
        return w >= h
            ? (Even(ShortSide * (double)w / h), ShortSide)
            : (ShortSide, Even(ShortSide * (double)h / w));
    }

    private static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);

    private static string Seconds(long ms) => (ms / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);

    private void OnPrerender(IntPtr data, ref IntPtr buffer, UIntPtr size)
    {
        var needed = (int)size.ToUInt64();
        if (needed > _bufferSize)
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = Marshal.AllocHGlobal(needed);
            _bufferSize = needed;
        }
        buffer = _buffer;
    }

    private void OnPostrender(IntPtr data, IntPtr buffer, int width, int height, int bitsPerPixel, UIntPtr size, long pts)
    {
        var handler = _onFrame;
        if (handler == null || bitsPerPixel != 32 || width <= 0 || height <= 0) return;
        try
        {
            // Timestamps are on VLC's clock, but advance exactly with the video within a run: the first picture is
            // the start position. VLC's fps filter hands each picture on one interval late (it outputs the previous
            // picture with the next timestamp), so its second output repeats the first and the rest are one
            // interval behind their timestamp (measured with a timecode video: then exact to < 0.1 s from 0:00,
            // ~0.3 s late after a seek, which lands slightly before the asked-for time).
            _firstPts ??= pts;
            var index = _outputIndex++;
            if (index == 1) return;
            var timeMs = _startMs + (pts - _firstPts.Value) / 1000 - (index == 0 ? 0 : SampleIntervalMs);

            VideoFrame? frame = null;
            while (_pool.TryTake(out var pooled))
            {
                if (pooled.Width == width && pooled.Height == height)
                {
                    frame = pooled;
                    break;
                }
            }
            frame ??= new VideoFrame(width, height);
            Marshal.Copy(buffer, frame.Pixels, 0, Math.Min(frame.Pixels.Length, (int)size.ToUInt64()));
            frame.Pts = pts;
            frame.TimeMs = timeMs;
            Observer?.Invoke(frame);
            handler(frame);
        }
        catch (Exception ex)
        {
            // An exception must never cross back into VLC's native thread.
            Log.Error(ex, "AI frame");
        }
    }

    public void Dispose()
    {
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _bufferSize = 0;
        }
    }
}
