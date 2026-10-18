using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;

namespace VibePlayer.Core.Ai.Onnx;

/// <summary>A chip the models can run on.</summary>
internal sealed class AiDevice
{
    public AiDevice(string kind, string name, string provider, OrtEpDevice? epDevice)
    {
        Kind = kind;
        Name = name;
        Provider = provider;
        EpDevice = epDevice;
    }

    /// <summary>NPU, GPU or CPU.</summary>
    public string Kind { get; }
    /// <summary>The hardware's name, e.g. "Intel(R) AI Boost".</summary>
    public string Name { get; }
    /// <summary>The ONNX Runtime execution provider, e.g. "OpenVINOExecutionProvider".</summary>
    public string Provider { get; }
    /// <summary>Null for ONNX Runtime's own CPU code.</summary>
    public OrtEpDevice? EpDevice { get; }

    public bool IsCpu => EpDevice == null;

    public override string ToString() => $"{Kind} ({Name})";
}

/// <summary>
/// Picks the best chip for the models. Windows ML first fetches the execution providers that Windows has
/// certified for this PC (Intel OpenVINO, Qualcomm QNN, AMD Vitis AI, NVIDIA TensorRT ...), which is what
/// makes NPUs usable; DirectML (any DirectX 12 GPU) and the CPU are always there.
/// Order for Auto: NPU, then the best GPU (a discrete one first), then the CPU.
/// </summary>
internal static class AiHardware
{
    private static readonly SemaphoreSlim ProvidersGate = new(1, 1);
    private static bool s_providersReady;

    /// <summary>Downloads (first time) and registers Windows ML's execution providers. Never throws.</summary>
    public static async Task EnsureProvidersAsync(IProgress<string>? status, CancellationToken ct)
    {
        await ProvidersGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (s_providersReady) return;
            s_providersReady = true; // one attempt per run; CPU and DirectML work without it
            var catalog = ExecutionProviderCatalog.GetDefault();
            foreach (var provider in catalog.FindAllProviders())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var state = provider.ReadyState;
                    if (state == ExecutionProviderReadyState.NotPresent)
                        status?.Report($"Getting the {FriendlyProvider(provider.Name)} driver from Windows (first time only)…");
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var result = await provider.EnsureReadyAsync();
                    var registered = result.Status == ExecutionProviderReadyResultState.Success && provider.TryRegister();
                    Log.Info($"Windows ML provider {provider.Name}: was {state}, {result.Status}, registered {registered} ({clock.ElapsedMilliseconds} ms)");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Windows ML provider " + provider.Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Windows ML missing or blocked (older Windows, policy): DirectML and the CPU still work.
            Log.Error(ex, "Windows ML execution providers");
        }
        finally
        {
            ProvidersGate.Release();
        }
    }

    /// <summary>The chips to try, best first, for the user's choice. The CPU is always last as the fallback.</summary>
    public static List<AiDevice> Candidates(AiProcessor preference)
    {
        var npus = new List<AiDevice>();
        var gpus = new List<(AiDevice Device, int Rank)>();
        var cpuName = "CPU";
        try
        {
            foreach (var d in OrtEnv.Instance().GetEpDevices())
            {
                var hw = d.HardwareDevice;
                var name = Describe(d);
                switch (hw.Type)
                {
                    case OrtHardwareDeviceType.NPU:
                        // "OpenVINOExecutionProvider.AUTO" lets OpenVINO pick: prefer the plain NPU entry.
                        if (!d.EpName.Contains(".")) npus.Insert(0, new AiDevice("NPU", name, d.EpName, d));
                        else npus.Add(new AiDevice("NPU", name, d.EpName, d));
                        break;
                    case OrtHardwareDeviceType.GPU:
                        if (d.EpName.Contains(".")) break;
                        gpus.Add((new AiDevice("GPU", name, d.EpName, d), GpuRank(d)));
                        break;
                    case OrtHardwareDeviceType.CPU:
                        if (d.EpName == "CPUExecutionProvider") cpuName = name;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "List AI devices");
        }

        var gpuList = gpus.OrderBy(g => g.Rank).Select(g => g.Device).ToList();
        var cpu = new AiDevice("CPU", cpuName, "CPUExecutionProvider", null);
        var list = preference switch
        {
            AiProcessor.Npu => npus.Concat(gpuList),
            AiProcessor.Gpu => gpuList,
            AiProcessor.Cpu => Enumerable.Empty<AiDevice>(),
            _ => npus.Concat(gpuList),
        };
        return list.Append(cpu).ToList();
    }

    /// <summary>
    /// Lower is better: discrete GPUs first; then NVIDIA's / AMD's own providers (much faster than DirectML on
    /// their cards), then DirectML, then the rest (OpenVINO's GPU path was slower than DirectML on Intel graphics).
    /// </summary>
    private static int GpuRank(OrtEpDevice d)
    {
        var discrete = Meta(d.HardwareDevice.Metadata, "Discrete") == "1"
                       || string.Equals(Meta(d.EpMetadata, "DEVICE_TYPE"), "discrete", StringComparison.OrdinalIgnoreCase);
        var provider = d.EpName switch
        {
            "NvTensorRTRTXExecutionProvider" or "NvTensorRtRtxExecutionProvider" or "MIGraphXExecutionProvider" => 0,
            "DmlExecutionProvider" => 1,
            _ => 2,
        };
        return (discrete ? 0 : 10) + provider;
    }

    private static string Describe(OrtEpDevice d)
    {
        var name = Meta(d.HardwareDevice.Metadata, "Description")
                   ?? Meta(d.EpMetadata, "FULL_DEVICE_NAME")
                   ?? d.HardwareDevice.Vendor;
        return string.IsNullOrWhiteSpace(name) ? d.HardwareDevice.Type.ToString() : name!.Trim();
    }

    private static string? Meta(OrtKeyValuePairs pairs, string key)
    {
        try { return pairs.Entries.TryGetValue(key, out var value) ? value : null; }
        catch { return null; }
    }

    private static string FriendlyProvider(string name) => name switch
    {
        "OpenVINOExecutionProvider" => "Intel OpenVINO",
        "QNNExecutionProvider" => "Qualcomm QNN",
        "VitisAIExecutionProvider" => "AMD Vitis AI",
        "MIGraphXExecutionProvider" => "AMD MIGraphX",
        _ when name.StartsWith("NvTensorRT", StringComparison.OrdinalIgnoreCase) => "NVIDIA TensorRT",
        _ => name.Replace("ExecutionProvider", ""),
    };

    /// <summary>Session settings for a device. Input sizes are fixed (NPUs need that; it also helps GPUs).</summary>
    public static SessionOptions Options(AiDevice device, IReadOnlyDictionary<string, long> dimensions)
    {
        var options = new SessionOptions
        {
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        foreach (var (name, value) in dimensions) options.AddFreeDimensionOverrideByName(name, value);

        if (device.EpDevice == null)
        {
            // Leave cores for video decoding and for playback.
            options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
            return options;
        }
        var providerOptions = new Dictionary<string, string>();
        if (device.Provider == "DmlExecutionProvider")
        {
            options.EnableMemoryPattern = false;
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
        }
        else if (device.Provider == "OpenVINOExecutionProvider")
        {
            // Keep compiled models between runs: compiling CLIP for an Intel NPU takes 10-20 s every start otherwise.
            var cache = Path.Combine(AppInfo.LocalDirectory, "openvino-cache");
            try
            {
                Directory.CreateDirectory(cache);
                var ovDevice = device.Kind == "NPU" ? "NPU" : device.Kind == "GPU" ? "GPU" : "CPU";
                providerOptions["load_config"] = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    [ovDevice] = new Dictionary<string, string> { ["CACHE_DIR"] = cache },
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "OpenVINO cache folder");
            }
        }
        options.AppendExecutionProvider(OrtEnv.Instance(), new[] { device.EpDevice }, providerOptions);
        return options;
    }
}
