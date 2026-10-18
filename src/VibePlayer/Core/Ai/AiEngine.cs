using System.Reflection;
using System.Security.Cryptography;

namespace VibePlayer.Core.Ai;

/// <summary>Scores one picture. Created once per process (loading the models takes a while) and reused.</summary>
public interface ISceneAnalyzer : IDisposable
{
    /// <summary>Which chips run the models, e.g. "NPU (Intel(R) AI Boost)".</summary>
    string Device { get; }

    /// <summary>Identifies models + prompts; scans made with another set are redone.</summary>
    string ModelSet { get; }

    /// <summary>Recent average time to score one picture.</summary>
    double MillisecondsPerFrame { get; }

    SceneSample Analyze(VideoFrame frame, long timeMs, float change);
}

/// <summary>
/// Entry point to the AI scene filter. The models run through Windows ML (ONNX Runtime) on the NPU, GPU or CPU;
/// that exists for 64-bit Windows 10 / 11 only, so the other builds report the filter as unavailable.
/// </summary>
public static class AiEngine
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static ISceneAnalyzer? s_analyzer;
#if VIBE_AI
    private static (AiProcessor Processor, bool NudeNet) s_analyzerKey;
#endif

#if VIBE_AI
    public static bool IsSupported => Environment.OSVersion.Version >= new Version(10, 0, 18362);

    public static string? UnsupportedReason => IsSupported ? null : "The AI scene filter needs Windows 10 version 1903 or newer.";
#else
    public static bool IsSupported => false;

    public static string? UnsupportedReason => "The AI scene filter needs 64-bit Windows 10 or 11 (x64 or ARM64).";
#endif

    /// <summary>The device of the loaded models, once a scan has started (null before).</summary>
    public static string? ActiveDevice => s_analyzer?.Device;

    /// <summary>Models + prompt set id, without loading anything (scans are cached under it).</summary>
    public static string ModelSetFor(bool nudeNet) => "clip-vit-b32+prompts-" + PromptsHash + (nudeNet ? "+nudenet-320n" : "");

    private static readonly Lazy<string> PromptsHashLazy = new(() =>
    {
        using var stream = OpenPrompts();
        using var sha = SHA1.Create();
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").Substring(0, 12).ToLowerInvariant();
    });

    private static string PromptsHash => PromptsHashLazy.Value;

    /// <summary>The CLIP prompt set with its text embeddings (Assets\ai-prompts.json, embedded).</summary>
    internal static Stream OpenPrompts() =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream("VibePlayer.ai-prompts.json")
        ?? throw new InvalidOperationException("ai-prompts.json is missing from the build.");

    /// <summary>
    /// The shared analyzer for these options: loads (and on first use downloads) the models, preparing the
    /// NPU / GPU on the way. <paramref name="status"/> gets short progress messages.
    /// </summary>
    public static async Task<ISceneAnalyzer> GetAnalyzerAsync(AiProcessor processor, bool nudeNet, IProgress<string>? status, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
#if VIBE_AI
            if (s_analyzer != null && s_analyzerKey == (processor, nudeNet)) return s_analyzer;
            s_analyzer?.Dispose();
            s_analyzer = null;
            s_analyzer = await Onnx.OnnxSceneAnalyzer.CreateAsync(processor, nudeNet, status, ct).ConfigureAwait(false);
            s_analyzerKey = (processor, nudeNet);
            return s_analyzer;
#else
            await Task.CompletedTask.ConfigureAwait(false);
            throw new PlatformNotSupportedException(UnsupportedReason);
#endif
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Unloads the models (e.g. after the processor setting changed).</summary>
    public static void Reset()
    {
        Gate.Wait();
        try
        {
            s_analyzer?.Dispose();
            s_analyzer = null;
        }
        finally
        {
            Gate.Release();
        }
    }
}
