using System.Collections.Concurrent;
using System.Diagnostics;

namespace VibePlayer.Core.Ai;

/// <summary>
/// Scans one video in the background: reads pictures (<see cref="FrameGrabber"/>), scores them
/// (<see cref="ISceneAnalyzer"/>) and adds the results to the <see cref="SceneScan"/>.
/// It scans from where the viewer is watching first, jumps ahead when they seek to a part that isn't
/// scanned yet, then fills in the rest. Decoding and scoring run in parallel; decoding waits when
/// scoring falls behind. Progress is saved, so a stopped scan continues where it left off.
/// </summary>
public sealed class SceneScanner
{
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MinRefocusInterval = TimeSpan.FromSeconds(3);
    /// <summary>A seek within this distance ahead of the scan doesn't restart it (it gets there soon anyway).</summary>
    private const long RefocusAheadMs = 90_000;

    private readonly string _path;
    private readonly AiProcessor _processor;
    private readonly bool _nudeNet;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _passCts;
    private long _focusMs;
    private long _passStartMs = -1;
    private long _frontierMs = -1;
    private DateTime _lastRefocus = DateTime.MinValue;
    private Task? _task;

    // Speed: video milliseconds scanned per real second, over the current run.
    private readonly Stopwatch _clock = new();
    private long _scannedMs;

    public SceneScanner(SceneScan scan, string path, AiProcessor processor, bool nudeNet, long focusMs = 0, bool saveToCache = true)
    {
        Scan = scan;
        _path = path;
        _processor = processor;
        _nudeNet = nudeNet;
        _focusMs = Math.Max(0, focusMs);
        _saveToCache = saveToCache;
    }

    private readonly bool _saveToCache;

    public SceneScan Scan { get; }

    /// <summary>A short line for the UI: "Loading AI models…", "Scanning…", or what went wrong.</summary>
    public string Status { get; private set; } = "Starting…";

    public string? Device { get; private set; }

    public bool IsRunning => _task is { IsCompleted: false };

    public bool Finished { get; private set; }

    public bool Failed { get; private set; }

    /// <summary>The file has no picture to scan (audio only).</summary>
    public bool NoVideo { get; private set; }

    /// <summary>How many seconds of video are scanned per second (e.g. 12 = 12x real time).</summary>
    public double Speed
    {
        get
        {
            var seconds = _clock.Elapsed.TotalSeconds;
            return seconds > 2 ? Interlocked.Read(ref _scannedMs) / 1000.0 / seconds : 0;
        }
    }

    public double MillisecondsPerFrame { get; private set; }

    /// <summary>Raised (on a background thread) when finished, failed or stopped.</summary>
    public event Action? Completed;

    public void Start()
    {
        if (_task != null) return;
        _task = Task.Run(RunAsync);
    }

    /// <summary>Stops scanning; what was scanned so far is saved.</summary>
    public void Cancel()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Waits (up to the timeout) for the scan to wind down after <see cref="Cancel"/>.</summary>
    public bool Wait(TimeSpan timeout)
    {
        try { return _task == null || _task.Wait(timeout); }
        catch { return true; }
    }

    /// <summary>
    /// Where the viewer is now. If that part isn't scanned and the current pass won't reach it soon,
    /// the scan jumps there.
    /// </summary>
    public void Focus(long ms)
    {
        lock (_gate)
        {
            _focusMs = Math.Max(0, ms);
            if (_passCts == null || _passStartMs < 0) return;
            if (Scan.IsCovered(ms)) return;
            var aheadOfPass = ms >= _passStartMs && ms <= Math.Max(_frontierMs, _passStartMs) + RefocusAheadMs;
            if (aheadOfPass) return;
            if (DateTime.UtcNow - _lastRefocus < MinRefocusInterval) return;
            _lastRefocus = DateTime.UtcNow;
            _passCts.Cancel();
        }
    }

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        using var grabber = new FrameGrabber();
        try
        {
            Status = "Reading the video…";
            var info = await grabber.ReadInfoAsync(_path, ct).ConfigureAwait(false);
            if (!info.HasVideo)
            {
                NoVideo = true;
                Status = "No picture to scan";
                return;
            }
            Scan.LengthMs = info.LengthMs;

            var analyzer = await AiEngine.GetAnalyzerAsync(_processor, _nudeNet, new SyncProgress(s => Status = s), ct).ConfigureAwait(false);
            Device = analyzer.Device;
            Scan.Device = analyzer.Device;

            Status = "Scanning…";
            _clock.Start();
            var lastSave = DateTime.UtcNow;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                long focus;
                lock (_gate) focus = _focusMs;
                var gap = Scan.NextGap(focus);
                if (gap == null) break;

                await ScanPassAsync(grabber, info, analyzer, gap.Value, ct).ConfigureAwait(false);
                if (DateTime.UtcNow - lastSave > SaveInterval)
                {
                    if (_saveToCache) Scan.Save();
                    lastSave = DateTime.UtcNow;
                }
                if (Scan.LengthMs <= 0) break; // unknown length: one pass to the end is all we can do
            }

            Finished = true;
            Status = "Done";
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AI scan " + _path);
            Failed = true;
            Status = ex is PlatformNotSupportedException ? ex.Message : "AI scan failed: " + ex.Message;
        }
        finally
        {
            _clock.Stop();
            if (_saveToCache) Scan.Save();
            try { Completed?.Invoke(); }
            catch (Exception ex) { Log.Error(ex, "AI scan completed handler"); }
        }
    }

    private async Task ScanPassAsync(FrameGrabber grabber, VideoInfo info, ISceneAnalyzer analyzer, TimeRange gap, CancellationToken ct)
    {
        using var passCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stopMs = gap.EndMs == long.MaxValue || (Scan.LengthMs > 0 && gap.EndMs >= Scan.LengthMs) ? 0 : gap.EndMs;
        lock (_gate)
        {
            _passCts = passCts;
            _passStartMs = gap.StartMs;
            _frontierMs = gap.StartMs;
        }

        // Scoring runs on its own thread so decoding the next pictures overlaps with it.
        using var queue = new BlockingCollection<(VideoFrame Frame, long TimeMs)>(boundedCapacity: 6);
        var lastTime = -1L;
        var scorer = Task.Factory.StartNew(() =>
        {
            float[]? previous = null;
            foreach (var (frame, time) in queue.GetConsumingEnumerable())
            {
                try
                {
                    if (passCts.IsCancellationRequested) continue;
                    var thumb = FrameMath.Thumbnail(frame);
                    var change = previous == null ? 0 : FrameMath.Difference(previous, thumb);
                    previous = thumb;
                    Scan.Add(analyzer.Analyze(frame, time, change));
                    MillisecondsPerFrame = analyzer.MillisecondsPerFrame;
                    if (lastTime >= 0 && time > lastTime) Interlocked.Add(ref _scannedMs, time - lastTime);
                    lastTime = time;
                    Scan.MarkCovered(gap.StartMs, time);
                    lock (_gate) _frontierMs = Math.Max(_frontierMs, time);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "AI analyze");
                }
                finally
                {
                    grabber.Recycle(frame);
                }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var completed = false;
        try
        {
            await grabber.RunAsync(_path, info, gap.StartMs, stopMs, frame =>
            {
                if (passCts.IsCancellationRequested)
                {
                    grabber.Recycle(frame);
                    return;
                }
                try
                {
                    queue.Add((frame, frame.TimeMs), passCts.Token); // waits while scoring catches up
                }
                catch (OperationCanceledException)
                {
                    grabber.Recycle(frame);
                }
            }, passCts.Token).ConfigureAwait(false);
            completed = true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Refocused: the loop picks the next part from the new position.
        }
        finally
        {
            queue.CompleteAdding();
            await scorer.ConfigureAwait(false);
            lock (_gate)
            {
                _passCts = null;
                _passStartMs = -1;
            }
        }

        // Reached the end of the part: all of it counts as scanned (the last half second has no new picture).
        if (completed && !passCts.IsCancellationRequested)
            Scan.MarkCovered(gap.StartMs, stopMs == 0 ? Math.Max(Scan.LengthMs, lastTime) : gap.EndMs);
    }

    /// <summary>IProgress that reports on the calling thread (Progress&lt;T&gt; would post to a sync context).</summary>
    private sealed class SyncProgress : IProgress<string>
    {
        private readonly Action<string> _report;
        public SyncProgress(Action<string> report) => _report = report;
        public void Report(string value) => _report(value);
    }
}
