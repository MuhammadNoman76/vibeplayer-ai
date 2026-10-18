using System.Diagnostics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VibePlayer.Core.Ai.Onnx;

/// <summary>
/// Scores pictures with two small models:
/// - CLIP ViT-B/32 (OpenAI, MIT): the picture's embedding is compared with text prompts ("two people kissing",
///   "a sex scene", "a naked person" ... and many ordinary-scene prompts to compete with); the share of each
///   category in the softmax is its score. The prompts' text embeddings are pre-computed (Assets\ai-prompts.json).
/// - NudeNet 320n (optional): a detector for exposed body parts; the score is its most confident exposed detection.
/// </summary>
internal sealed class OnnxSceneAnalyzer : ISceneAnalyzer
{
    private const int ClipSize = 224;
    private const int NudeSize = 320;

    private static readonly float[] ClipMean = { 0.48145466f, 0.4578275f, 0.40821073f };
    private static readonly float[] ClipStd = { 0.26862954f, 0.26130258f, 0.27577711f };

    /// <summary>NudeNet classes that count: buttocks, female breast, female genitalia, anus, male genitalia (exposed).</summary>
    private static readonly int[] ExposedClasses = { 2, 3, 4, 6, 14 };

    private enum Category { Kiss, Sex, Nudity, Neutral }

    private readonly InferenceSession _clip;
    private readonly InferenceSession? _nude;
    private readonly DenseTensor<float> _clipInput = new(new[] { 1, 3, ClipSize, ClipSize });
    private readonly DenseTensor<float> _nudeInput = new(new[] { 1, 3, NudeSize, NudeSize });
    private readonly List<NamedOnnxValue> _clipInputs;
    private readonly List<NamedOnnxValue>? _nudeInputs;
    private readonly float[][] _prompts;
    private readonly Category[] _promptCategory;
    private readonly float _logitScale;
    private double _msPerFrame;

    private OnnxSceneAnalyzer(InferenceSession clip, AiDevice clipDevice, InferenceSession? nude, AiDevice? nudeDevice,
        float[][] prompts, Category[] promptCategory, float logitScale, bool nudeNet)
    {
        _clip = clip;
        _nude = nude;
        _prompts = prompts;
        _promptCategory = promptCategory;
        _logitScale = logitScale;
        _clipInputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("pixel_values", _clipInput) };
        if (nude != null) _nudeInputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("images", _nudeInput) };

        Device = nudeDevice == null || nudeDevice.ToString() == clipDevice.ToString()
            ? clipDevice.ToString()
            : $"{clipDevice} + NudeNet on {nudeDevice}";
        ModelSet = AiEngine.ModelSetFor(nudeNet);
    }

    public string Device { get; }

    public string ModelSet { get; }

    public double MillisecondsPerFrame => _msPerFrame;

    public static async Task<OnnxSceneAnalyzer> CreateAsync(AiProcessor processor, bool nudeNet, IProgress<string>? status, CancellationToken ct)
    {
        status?.Report("Looking for the best chip for the AI…");
        await AiHardware.EnsureProvidersAsync(status, ct).ConfigureAwait(false);
        var (prompts, categories, scale) = LoadPrompts();
        var candidates = AiHardware.Candidates(processor);

        var clipDims = new Dictionary<string, long> { ["batch_size"] = 1, ["num_channels"] = 3, ["height"] = ClipSize, ["width"] = ClipSize };
        var (clip, clipDevice) = await OpenAsync(candidates, d => d.IsCpu ? ModelStore.ClipFp32 : ModelStore.ClipFp16,
            "pixel_values", ClipSize, clipDims, status, ct).ConfigureAwait(false);

        InferenceSession? nude = null;
        AiDevice? nudeDevice = null;
        if (nudeNet)
        {
            try
            {
                var nudeDims = new Dictionary<string, long> { ["batch"] = 1, ["height"] = NudeSize, ["width"] = NudeSize };
                (nude, nudeDevice) = await OpenAsync(candidates, _ => ModelStore.NudeNet, "images", NudeSize, nudeDims, status, ct).ConfigureAwait(false);
            }
            catch
            {
                clip.Dispose();
                throw;
            }
        }
        return new OnnxSceneAnalyzer(clip, clipDevice, nude, nudeDevice, prompts, categories, scale, nudeNet);
    }

    /// <summary>Opens the model on the first device that loads it and gives sane results.</summary>
    private static async Task<(InferenceSession Session, AiDevice Device)> OpenAsync(List<AiDevice> candidates,
        Func<AiDevice, AiModel> modelFor, string inputName, int size, IReadOnlyDictionary<string, long> dims,
        IProgress<string>? status, CancellationToken ct)
    {
        var failures = new List<string>();
        foreach (var device in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var model = modelFor(device);
            var path = await ModelStore.EnsureAsync(model, status, ct).ConfigureAwait(false);
            status?.Report(device.IsCpu
                ? $"Loading the {model.Name} on the CPU…"
                : $"Preparing the {model.Name} for the {device.Kind} ({device.Name})… the first time can take a minute");

            InferenceSession? session = null;
            try
            {
                Log.Info($"AI model {model.FileName}: opening on {device} [{device.Provider}]");
                var clock = Stopwatch.StartNew();
                session = await Task.Run(() =>
                {
                    using var options = AiHardware.Options(device, dims);
                    return new InferenceSession(path, options);
                }, ct).ConfigureAwait(false);
                var loadMs = clock.ElapsedMilliseconds;
                var runMs = Validate(session, inputName, size);
                Log.Info($"AI model {model.FileName} on {device} [{device.Provider}]: loaded in {loadMs} ms, {runMs:0.0} ms per picture");
                return (session, device);
            }
            catch (OperationCanceledException)
            {
                session?.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                session?.Dispose();
                Log.Error(ex, $"AI model {model.FileName} on {device} [{device.Provider}]");
                failures.Add($"{device}: {ex.Message.Split('\n')[0]}");
            }
        }
        throw new InvalidOperationException("the AI model couldn't run on this PC (" + string.Join("; ", failures) + ")");
    }

    /// <summary>Runs a gray picture a few times: the output must be finite and not all zero. Returns ms per run.</summary>
    private static double Validate(InferenceSession session, string inputName, int size)
    {
        var input = new DenseTensor<float>(new[] { 1, 3, size, size });
        input.Buffer.Span.Fill(0.5f);
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, input) };
        using (var warmup = session.Run(inputs))
        {
            var values = warmup[0].AsEnumerable<float>().ToArray();
            if (values.Length == 0 || values.Any(v => float.IsNaN(v) || float.IsInfinity(v)) || values.All(v => v == 0))
                throw new InvalidDataException("the model gave invalid results on this device");
        }
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
            using (session.Run(inputs)) { }
        return clock.Elapsed.TotalMilliseconds / 3;
    }

    private static (float[][] Prompts, Category[] Categories, float Scale) LoadPrompts()
    {
        using var stream = AiEngine.OpenPrompts();
        using var doc = JsonDocument.Parse(stream);
        var scale = doc.RootElement.GetProperty("logitScale").GetSingle();
        var prompts = new List<float[]>();
        var categories = new List<Category>();
        foreach (var category in doc.RootElement.GetProperty("categories").EnumerateObject())
        {
            var which = category.Name switch
            {
                "kiss" => Category.Kiss,
                "sex" => Category.Sex,
                "nudity" => Category.Nudity,
                _ => Category.Neutral,
            };
            foreach (var prompt in category.Value.EnumerateArray())
            {
                prompts.Add(prompt.GetProperty("embedding").EnumerateArray().Select(e => e.GetSingle()).ToArray());
                categories.Add(which);
            }
        }
        return (prompts.ToArray(), categories.ToArray(), scale);
    }

    public SceneSample Analyze(VideoFrame frame, long timeMs, float change)
    {
        var clock = Stopwatch.StartNew();

        // CLIP: which kind of picture is this?
        FillClipInput(frame);
        float[] embedding;
        using (var results = _clip.Run(_clipInputs))
            embedding = results[0].AsEnumerable<float>().ToArray();
        var norm = 0f;
        foreach (var v in embedding) norm += v * v;
        norm = (float)Math.Sqrt(Math.Max(norm, 1e-12f));

        var logits = new float[_prompts.Length];
        var max = float.MinValue;
        for (var p = 0; p < _prompts.Length; p++)
        {
            var dot = 0f;
            var prompt = _prompts[p];
            for (var i = 0; i < prompt.Length && i < embedding.Length; i++) dot += prompt[i] * embedding[i];
            logits[p] = _logitScale * dot / norm;
            max = Math.Max(max, logits[p]);
        }
        var sums = new float[4];
        var total = 0f;
        for (var p = 0; p < logits.Length; p++)
        {
            var e = (float)Math.Exp(logits[p] - max);
            sums[(int)_promptCategory[p]] += e;
            total += e;
        }

        // NudeNet: any exposed body part?
        var nude = 0f;
        if (_nude != null && _nudeInputs != null)
        {
            FillNudeInput(frame);
            using var results = _nude.Run(_nudeInputs);
            var output = results[0].AsTensor<float>();
            var anchors = output.Dimensions[2];    // [1, 4 box + 18 classes, anchors]
            var data = (output as DenseTensor<float> ?? output.ToDenseTensor()).Buffer.Span;
            foreach (var cls in ExposedClasses)
            {
                var row = (4 + cls) * anchors;
                for (var a = 0; a < anchors; a++)
                    if (data[row + a] > nude) nude = data[row + a];
            }
        }

        var ms = clock.Elapsed.TotalMilliseconds;
        _msPerFrame = _msPerFrame <= 0 ? ms : _msPerFrame * 0.9 + ms * 0.1;
        return new SceneSample(timeMs,
            sums[(int)Category.Kiss] / total,
            sums[(int)Category.Sex] / total,
            sums[(int)Category.Nudity] / total,
            nude,
            change);
    }

    /// <summary>CLIP input: centre square, 224×224, RGB, normalised with CLIP's mean / std.</summary>
    private void FillClipInput(VideoFrame f)
    {
        var dst = _clipInput.Buffer.Span;
        int w = f.Width, h = f.Height;
        var side = Math.Min(w, h);
        float x0 = (w - side) / 2f, y0 = (h - side) / 2f, scale = side / (float)ClipSize;
        const int plane = ClipSize * ClipSize;
        var px = f.Pixels;
        for (var oy = 0; oy < ClipSize; oy++)
        {
            var sy = MathEx.Clamp(y0 + (oy + 0.5f) * scale - 0.5f, 0f, h - 1f);
            int iy = (int)sy, iy1 = Math.Min(iy + 1, h - 1);
            var fy = sy - iy;
            for (var ox = 0; ox < ClipSize; ox++)
            {
                var sx = MathEx.Clamp(x0 + (ox + 0.5f) * scale - 0.5f, 0f, w - 1f);
                int ix = (int)sx, ix1 = Math.Min(ix + 1, w - 1);
                var fx = sx - ix;
                int p00 = (iy * w + ix) * 4, p01 = (iy * w + ix1) * 4, p10 = (iy1 * w + ix) * 4, p11 = (iy1 * w + ix1) * 4;
                var o = oy * ClipSize + ox;
                for (var c = 0; c < 3; c++)
                {
                    var k = 2 - c; // pixels are B, G, R, X; the model wants R, G, B
                    var top = px[p00 + k] + (px[p01 + k] - px[p00 + k]) * fx;
                    var bottom = px[p10 + k] + (px[p11 + k] - px[p10 + k]) * fx;
                    var v = (top + (bottom - top) * fy) / 255f;
                    dst[c * plane + o] = (v - ClipMean[c]) / ClipStd[c];
                }
            }
        }
    }

    /// <summary>NudeNet input: the whole picture padded to a square (black at the bottom / right), 320×320, RGB 0..1.</summary>
    private void FillNudeInput(VideoFrame f)
    {
        var dst = _nudeInput.Buffer.Span;
        int w = f.Width, h = f.Height;
        var scale = Math.Max(w, h) / (float)NudeSize;
        const int plane = NudeSize * NudeSize;
        var px = f.Pixels;
        for (var oy = 0; oy < NudeSize; oy++)
        {
            var syRaw = (oy + 0.5f) * scale - 0.5f;
            var inY = syRaw <= h - 0.5f;
            var sy = MathEx.Clamp(syRaw, 0f, h - 1f);
            int iy = (int)sy, iy1 = Math.Min(iy + 1, h - 1);
            var fy = sy - iy;
            for (var ox = 0; ox < NudeSize; ox++)
            {
                var o = oy * NudeSize + ox;
                var sxRaw = (ox + 0.5f) * scale - 0.5f;
                if (!inY || sxRaw > w - 0.5f)
                {
                    dst[o] = dst[plane + o] = dst[2 * plane + o] = 0f;
                    continue;
                }
                var sx = MathEx.Clamp(sxRaw, 0f, w - 1f);
                int ix = (int)sx, ix1 = Math.Min(ix + 1, w - 1);
                var fx = sx - ix;
                int p00 = (iy * w + ix) * 4, p01 = (iy * w + ix1) * 4, p10 = (iy1 * w + ix) * 4, p11 = (iy1 * w + ix1) * 4;
                for (var c = 0; c < 3; c++)
                {
                    var k = 2 - c;
                    var top = px[p00 + k] + (px[p01 + k] - px[p00 + k]) * fx;
                    var bottom = px[p10 + k] + (px[p11 + k] - px[p10 + k]) * fx;
                    dst[c * plane + o] = (top + (bottom - top) * fy) / 255f;
                }
            }
        }
    }

    public void Dispose()
    {
        _clip.Dispose();
        _nude?.Dispose();
    }
}
