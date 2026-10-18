using System.Diagnostics;
using System.Text.Json;

namespace VibePlayer.Core.Ai;

/// <summary>
/// Scans a file without opening a window and writes a JSON report (for testing the AI filter):
///   VibeVideoAI.exe --ai-scan "movie.mp4" [--ai-out report.json] [--ai-processor auto|npu|gpu|cpu] [--ai-no-nudenet]
///                   [--ai-focus seconds] [--ai-load-only]
/// The report has the chip used, the speed, every sample's scores and the scenes found at each sensitivity.
/// Nothing is read from or written to the scan cache.
/// </summary>
internal static class AiHeadless
{
    public const string Switch = "--ai-scan";

    public static int Run(string[] args) => Task.Run(() => RunAsync(args)).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string[] args)
    {
        string? Arg(string name)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var file = Arg(Switch);
        var output = Arg("--ai-out") ?? Path.Combine(AppInfo.RoamingDirectory, "ai-scan-report.json");
        var processor = Enum.TryParse<AiProcessor>(Arg("--ai-processor") ?? "Auto", true, out var p) ? p : AiProcessor.Auto;
        var nudeNet = !args.Any(a => string.Equals(a, "--ai-no-nudenet", StringComparison.OrdinalIgnoreCase));
        var report = new Dictionary<string, object?>
        {
            ["file"] = file,
            ["processor"] = processor.ToString(),
            ["nudeNet"] = nudeNet,
            ["started"] = DateTime.Now,
        };
        var statusLog = new List<string>();
        var exitCode = 0;
        var clock = Stopwatch.StartNew();
        try
        {
            if (args.Any(a => string.Equals(a, "--ai-load-only", StringComparison.OrdinalIgnoreCase)))
            {
                // Just load the models and score one gray picture (checks the chips; with a file, after reading its first picture).
                if (file != null && File.Exists(file))
                {
                    using var grabber = new FrameGrabber();
                    var info = await grabber.ReadInfoAsync(Path.GetFullPath(file), CancellationToken.None).ConfigureAwait(false);
                    report["video"] = new { info.HasVideo, info.Width, info.Height, info.LengthMs };
                }
                var analyzer = await AiEngine.GetAnalyzerAsync(processor, nudeNet, new LogProgress(statusLog, clock), CancellationToken.None).ConfigureAwait(false);
                var frame = new VideoFrame(568, 320);
                for (var i = 0; i < frame.Pixels.Length; i++) frame.Pixels[i] = 128;
                var sample = analyzer.Analyze(frame, 0, 0);
                report["device"] = analyzer.Device;
                report["grayFrame"] = new[] { sample.Kiss, sample.Sex, sample.NudeClip, sample.NudeNet };
                report["msPerFrame"] = analyzer.MillisecondsPerFrame;
                report["statusLog"] = statusLog;
                File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (file == null || !File.Exists(file)) throw new FileNotFoundException("File not found", file);
            var scan = SceneScan.CreateNew(Path.GetFullPath(file), AiEngine.ModelSetFor(nudeNet));
            // --ai-focus 65: start where a viewer resumed at 1:05 (the scan does that part first, then the rest).
            var focusMs = double.TryParse(Arg("--ai-focus"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var focus)
                ? (long)(focus * 1000) : 0;
            report["focusMs"] = focusMs;

            // --ai-timecode: the test video carries its own time as 16 black/white squares along the top
            // (tenths of a second, lowest bit on the left; see tools\ai); compare it with the time we assign.
            var timecodes = new System.Collections.Concurrent.ConcurrentQueue<(long Assigned, long Actual)>();
            if (args.Any(a => string.Equals(a, "--ai-timecode", StringComparison.OrdinalIgnoreCase)))
                FrameGrabber.Observer = frame => timecodes.Enqueue((frame.TimeMs, ReadTimecode(frame)));

            var scanner = new SceneScanner(scan, scan.Source, processor, nudeNet, focusMs, saveToCache: false);
            scanner.Start();

            // Record status changes (model download, NPU preparation ...) while waiting.
            var last = "";
            while (scanner.IsRunning || !scanner.Wait(TimeSpan.Zero))
            {
                if (scanner.Status != last)
                {
                    last = scanner.Status;
                    statusLog.Add($"{clock.Elapsed.TotalSeconds:0.0}s {last}");
                    Log.Info($"AI test scan: {clock.Elapsed.TotalSeconds:0.0}s {last}");
                }
                await Task.Delay(100).ConfigureAwait(false);
            }
            scanner.Wait(TimeSpan.FromSeconds(5));
            statusLog.Add($"{clock.Elapsed.TotalSeconds:0.0}s {scanner.Status}");

            var samples = scan.Snapshot();
            report["status"] = scanner.Status;
            report["failed"] = scanner.Failed;
            report["device"] = scanner.Device;
            report["lengthMs"] = scan.LengthMs;
            report["covered"] = scan.CoveredFraction;
            report["msPerFrame"] = Math.Round(scanner.MillisecondsPerFrame, 2);
            report["speedX"] = Math.Round(scanner.Speed, 2);
            report["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 2);
            report["sampleCount"] = samples.Length;
            report["scenes"] = Enum.GetValues(typeof(AiSensitivity)).Cast<AiSensitivity>().ToDictionary(
                s => s.ToString(),
                s => SceneFinder.Find(samples, SceneKind.Sexual | SceneKind.Kissing, s, null, scan.LengthMs)
                    .Select(x => new { x.StartMs, x.EndMs, Kind = x.KindText, Peak = Math.Round(x.Peak, 2) }).ToList());
            report["samples"] = samples.Select(x => new[]
            {
                x.TimeMs, Round(x.Kiss), Round(x.Sex), Round(x.NudeClip), Round(x.NudeNet), Round(x.Change),
            }).ToList();
            report["sampleColumns"] = "timeMs, kiss, sex, nudeClip, nudeNet, change (x1000)";
            if (!timecodes.IsEmpty)
            {
                var errors = timecodes.Select(t => t.Assigned - t.Actual).ToList();
                var abs = errors.Select(Math.Abs).OrderBy(e => e).ToList();
                report["timecode"] = new
                {
                    Frames = errors.Count,
                    MeanErrorMs = Math.Round(errors.Average()),
                    MedianAbsMs = abs[abs.Count / 2],
                    P95AbsMs = abs[(int)(abs.Count * 0.95)],
                    MaxAbsMs = abs[^1],
                    Worst = timecodes.OrderByDescending(t => Math.Abs(t.Assigned - t.Actual)).Take(5).Select(t => new[] { t.Assigned, t.Actual }).ToList(),
                    Every50th = timecodes.Where((_, i) => i % 50 == 0).Select(t => new[] { t.Assigned, t.Actual }).ToList(),
                };
            }
            if (scanner.Failed) exitCode = 2;
        }
        catch (Exception ex)
        {
            report["error"] = ex.ToString();
            exitCode = 1;
        }
        report["statusLog"] = statusLog;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "AI scan report");
            exitCode = exitCode == 0 ? 3 : exitCode;
        }
        return exitCode;
    }

    private static long Round(float value) => (long)Math.Round(value * 1000);

    /// <summary>Reads the time burned into a timecode test frame (16 squares, each 1/16 of the width, 1/9 of the height).</summary>
    private static long ReadTimecode(VideoFrame f)
    {
        long value = 0;
        var y = f.Height * 40 / 720;
        for (var k = 0; k < 16; k++)
        {
            var x = f.Width * (k * 80 + 40) / 1280;
            var i = (y * f.Width + x) * 4;
            var gray = (f.Pixels[i] + f.Pixels[i + 1] + f.Pixels[i + 2]) / 3;
            if (gray > 128) value |= 1L << k;
        }
        return value * 100;
    }

    private sealed class LogProgress : IProgress<string>
    {
        private readonly List<string> _log;
        private readonly Stopwatch _clock;

        public LogProgress(List<string> log, Stopwatch clock)
        {
            _log = log;
            _clock = clock;
        }

        public void Report(string value)
        {
            _log.Add($"{_clock.Elapsed.TotalSeconds:0.0}s {value}");
            Log.Info($"AI test: {_clock.Elapsed.TotalSeconds:0.0}s {value}");
        }
    }
}
