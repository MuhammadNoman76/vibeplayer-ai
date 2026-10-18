using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace VibePlayer.Core.Ai.Onnx;

/// <summary>An AI model file the app downloads on first use (never shipped in the installer).</summary>
internal sealed class AiModel
{
    public AiModel(string name, string fileName, string url, long downloadSize, string downloadSha256,
        long fileSize, string fileSha256, string? zipEntry = null)
    {
        Name = name;
        FileName = fileName;
        Url = url;
        DownloadSize = downloadSize;
        DownloadSha256 = downloadSha256;
        FileSize = fileSize;
        FileSha256 = fileSha256;
        ZipEntry = zipEntry;
    }

    public string Name { get; }
    public string FileName { get; }
    public string Url { get; }
    public long DownloadSize { get; }
    public string DownloadSha256 { get; }
    public long FileSize { get; }
    public string FileSha256 { get; }
    /// <summary>For models that come inside a zip (a Python wheel): the file to take out of it.</summary>
    public string? ZipEntry { get; }
}

/// <summary>
/// Downloads the models into %LOCALAPPDATA%\VibeVideoAI\models (or uses copies next to the app in "models\")
/// and checks every file's size and SHA-256 before it is used.
/// </summary>
internal static class ModelStore
{
    /// <summary>For testing: a different models folder.</summary>
    public const string DirectoryOverrideVariable = "VIBEVIDEOAI_MODELS";

    /// <summary>
    /// NudeNet 3.4 detector (exposed / covered body parts), 320 px. It comes from the "nudenet" Python package on
    /// PyPI, whose 320n.onnx is the same file as the project's v3.4 release. NudeNet's licence is AGPL-3.0.
    /// </summary>
    public static readonly AiModel NudeNet = new(
        "nudity detector (NudeNet)", "nudenet-320n.onnx",
        "https://files.pythonhosted.org/packages/1c/ee/1aa02d44ba958cc77e16ff1e41a0aac5e721037db7bf62b9c9d124917f87/nudenet-3.4.2-py3-none-any.whl",
        10_595_223, "5937dbd84e5d8e5de038f08ffea5a1bb50a08475776bf2b4795914ce0eaf0331",
        12_150_158, "c15d8273adad2d0a92f014cc69ab2d6c311a06777a55545f2c4eb46f51911f0f",
        zipEntry: "nudenet/320n.onnx");

    /// <summary>OpenAI CLIP ViT-B/32 image encoder (MIT), float16 - for NPUs and GPUs.</summary>
    public static readonly AiModel ClipFp16 = new(
        "scene model (CLIP)", "clip-vit-b32-vision-fp16.onnx",
        "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/onnx/vision_model_fp16.onnx",
        176_080_659, "35c4e0fb0aeee527dcde1693520b214a34424a786babd530f35366bad5844efd",
        176_080_659, "35c4e0fb0aeee527dcde1693520b214a34424a786babd530f35366bad5844efd");

    /// <summary>
    /// The same CLIP image encoder, float32 - for the CPU (ONNX Runtime's CPU code can't load the fp16 file).
    /// The smaller 8-bit version is not used: its scores differ too much (false kisses, missed ones).
    /// </summary>
    public static readonly AiModel ClipFp32 = new(
        "scene model (CLIP)", "clip-vit-b32-vision-fp32.onnx",
        "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/onnx/vision_model.onnx",
        351_685_709, "fd6e1402a588279d1723c7534d4bcba5bc0b14b47dfab0e46f8c47b8270d7d40",
        351_685_709, "fd6e1402a588279d1723c7534d4bcba5bc0b14b47dfab0e46f8c47b8270d7d40");

    private static readonly HttpClient Http = CreateHttp();
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);

    public static string Directory =>
        Environment.GetEnvironmentVariable(DirectoryOverrideVariable) is { Length: > 0 } custom
            ? custom
            : Path.Combine(AppInfo.LocalDirectory, "models");

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Id}/{Updater.CurrentVersionText}");
        return http;
    }

    /// <summary>The model's path, downloading and verifying it first if needed.</summary>
    public static async Task<string> EnsureAsync(AiModel model, IProgress<string>? status, CancellationToken ct)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "models", model.FileName);
        if (IsValid(bundled, model)) return bundled;

        var path = Path.Combine(Directory, model.FileName);
        if (IsValid(path, model)) return path;

        await DownloadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsValid(path, model)) return path;
            System.IO.Directory.CreateDirectory(Directory);
            var download = path + ".download";
            try
            {
                await DownloadAsync(model, download, status, ct).ConfigureAwait(false);
                if (model.ZipEntry != null)
                {
                    var extracted = path + ".extract";
                    using (var zip = ZipFile.OpenRead(download))
                    {
                        var entry = zip.GetEntry(model.ZipEntry) ?? throw new InvalidDataException($"{model.ZipEntry} is missing from the download.");
                        entry.ExtractToFile(extracted, overwrite: true);
                    }
                    File.Delete(download);
                    download = extracted;
                }
                if (new FileInfo(download).Length != model.FileSize || !HashMatches(download, model.FileSha256))
                    throw new InvalidDataException($"The {model.Name} file didn't pass the check.");
                Compat.MoveOverwrite(download, path);
                MarkVerified(path, model);
            }
            finally
            {
                TryDelete(download);
            }
            return path;
        }
        finally
        {
            DownloadGate.Release();
        }
    }

    private static async Task DownloadAsync(AiModel model, string target, IProgress<string>? status, CancellationToken ct)
    {
        var sizeMb = model.DownloadSize / (1024.0 * 1024.0);
        status?.Report($"Downloading the {model.Name}… 0% of {sizeMb:0} MB (first time only)");

        using var response = await Http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            long total = 0;
            var lastPercent = -1;
            while (true)
            {
                // A stalled connection gives up after a minute instead of hanging forever.
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(TimeSpan.FromSeconds(60));
                var read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                sha.AppendData(buffer, 0, read);
                total += read;
                if (total > model.DownloadSize) throw new InvalidDataException($"The {model.Name} download is larger than expected.");
                var percent = (int)(total * 100 / model.DownloadSize);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    status?.Report($"Downloading the {model.Name}… {percent}% of {sizeMb:0} MB (first time only)");
                }
            }
            if (total != model.DownloadSize) throw new InvalidDataException($"The {model.Name} download is incomplete.");
        }
        if (!string.Equals(Convert.ToHexString(sha.GetHashAndReset()), model.DownloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The {model.Name} download didn't pass the SHA-256 check.");
    }

    /// <summary>
    /// Right size, and verified once (a small marker remembers it, so a 176 MB file isn't hashed on every start).
    /// </summary>
    private static bool IsValid(string path, AiModel model)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != model.FileSize) return false;
            var marker = path + ".verified";
            var expected = $"{model.FileSha256}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            if (File.Exists(marker) && File.ReadAllText(marker) == expected) return true;
            if (!HashMatches(path, model.FileSha256)) return false;
            MarkVerified(path, model);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void MarkVerified(string path, AiModel model)
    {
        try
        {
            var info = new FileInfo(path);
            File.WriteAllText(path + ".verified", $"{model.FileSha256}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
        }
        catch
        {
            // the bundled folder may be read-only: it is simply checked again next time
        }
    }

    private static bool HashMatches(string path, string sha256)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return string.Equals(Convert.ToHexString(sha.ComputeHash(stream)), sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
