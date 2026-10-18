using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VibePlayer.Core.Ai;

/// <summary>
/// Everything the AI found in one video file: a score sample every half second, which parts were scanned,
/// and the user's choices (scenes not to skip, scenes with moved edges). Cached in %LOCALAPPDATA%\VibeVideoAI\scans, so a file is
/// only scanned once and changing the sensitivity doesn't need a new scan.
/// Thread-safe: the scanner adds samples while the UI reads them.
/// </summary>
public sealed class SceneScan
{
    private const int FormatVersion = 1;
    private const int MaxCacheFiles = 1000;
    /// <summary>Gaps shorter than this (between scanned parts) don't need another pass.</summary>
    private const long MinGapMs = 1500;

    private readonly object _gate = new();
    private readonly List<SceneSample> _samples = new();
    private readonly List<TimeRange> _covered = new();
    private readonly List<TimeRange> _ignored = new();
    private readonly List<SceneEdit> _edits = new();
    private bool _sorted = true;
    private long _lengthMs;
    private string? _device;
    private int _version;

    private SceneScan(string source, string modelSet)
    {
        Source = source;
        ModelSet = modelSet;
    }

    public string Source { get; }

    /// <summary>Which models and prompts produced the samples; a different set means a new scan.</summary>
    public string ModelSet { get; }

    /// <summary>Goes up whenever something changes (lets the UI refresh only when needed).</summary>
    public int Version { get { lock (_gate) return _version; } }

    public long LengthMs
    {
        get { lock (_gate) return _lengthMs; }
        set { lock (_gate) { if (value > 0 && value != _lengthMs) { _lengthMs = value; _version++; } } }
    }

    /// <summary>The chip that did the scan, e.g. "NPU (Intel(R) AI Boost)".</summary>
    public string? Device
    {
        get { lock (_gate) return _device; }
        set { lock (_gate) _device = value; }
    }

    public int SampleCount { get { lock (_gate) return _samples.Count; } }

    public static SceneScan CreateNew(string source, string modelSet) => new(source, modelSet);

    /// <summary>A fresh scan of the same file that keeps the user's choices ("don't skip" and moved edges).</summary>
    public SceneScan Restart(string modelSet)
    {
        var fresh = new SceneScan(Source, modelSet);
        lock (_gate)
        {
            fresh._ignored.AddRange(_ignored);
            fresh._edits.AddRange(_edits);
        }
        return fresh;
    }

    // ------------------------------------------------------------------ samples

    public void Add(SceneSample sample)
    {
        lock (_gate)
        {
            if (_samples.Count > 0 && sample.TimeMs < _samples[^1].TimeMs) _sorted = false;
            _samples.Add(sample);
            _version++;
        }
    }

    /// <summary>All samples in time order (a copy).</summary>
    public SceneSample[] Snapshot()
    {
        lock (_gate)
        {
            if (!_sorted)
            {
                // Passes can overlap a little: keep one sample per time.
                var ordered = _samples.OrderBy(s => s.TimeMs).ToList();
                _samples.Clear();
                foreach (var s in ordered)
                    if (_samples.Count == 0 || s.TimeMs - _samples[^1].TimeMs >= 100) _samples.Add(s);
                _sorted = true;
            }
            return _samples.ToArray();
        }
    }

    // ------------------------------------------------------------------ coverage

    public void MarkCovered(long startMs, long endMs)
    {
        if (endMs <= startMs) return;
        lock (_gate)
        {
            AddRange(_covered, new TimeRange(Math.Max(0, startMs), endMs));
            _version++;
        }
    }

    public bool IsCovered(long ms)
    {
        lock (_gate) return _covered.Any(r => ms >= r.StartMs && ms <= r.EndMs);
    }

    /// <summary>Share of the video that has been scanned (0..1).</summary>
    public double CoveredFraction
    {
        get
        {
            lock (_gate)
            {
                if (_lengthMs <= 0) return 0;
                long sum = 0;
                foreach (var r in _covered) sum += Math.Max(0, Math.Min(r.EndMs, _lengthMs) - Math.Max(0, r.StartMs));
                return MathEx.Clamp((double)sum / _lengthMs, 0, 1);
            }
        }
    }

    public bool IsComplete => LengthMs > 0 && NextGap(0) == null;

    /// <summary>
    /// The next part that still needs scanning: the gap that contains <paramref name="focusMs"/> (from there on),
    /// else the first gap after it, else the first gap from the start. Null when everything is scanned.
    /// </summary>
    public TimeRange? NextGap(long focusMs)
    {
        lock (_gate)
        {
            if (_lengthMs <= 0) return _covered.Count == 0 ? new TimeRange(Math.Max(0, focusMs), long.MaxValue) : null;

            var gaps = new List<TimeRange>();
            long cursor = 0;
            foreach (var r in _covered.OrderBy(r => r.StartMs))
            {
                if (r.StartMs - cursor >= MinGapMs) gaps.Add(new TimeRange(cursor, r.StartMs));
                cursor = Math.Max(cursor, r.EndMs);
            }
            if (_lengthMs - cursor >= MinGapMs) gaps.Add(new TimeRange(cursor, _lengthMs));
            if (gaps.Count == 0) return null;

            focusMs = MathEx.Clamp(focusMs, 0, _lengthMs);
            foreach (var g in gaps)
            {
                if (focusMs >= g.StartMs && focusMs < g.EndMs)
                    return g.EndMs - focusMs >= MinGapMs ? new TimeRange(focusMs, g.EndMs) : g;
                if (g.StartMs >= focusMs) return g;
            }
            return gaps[0];
        }
    }

    // ------------------------------------------------------------------ ignored scenes

    public TimeRange[] Ignored { get { lock (_gate) return _ignored.ToArray(); } }

    public void Ignore(TimeRange range)
    {
        lock (_gate)
        {
            _ignored.Add(range);
            _version++;
        }
    }

    /// <summary>Skip this scene again (forgets "don't skip" choices that overlap it).</summary>
    public void Unignore(TimeRange range)
    {
        lock (_gate)
        {
            _ignored.RemoveAll(r => r.Overlap(range) > 0);
            _version++;
        }
    }

    public void ClearIgnored()
    {
        lock (_gate)
        {
            _ignored.Clear();
            _version++;
        }
    }

    // ------------------------------------------------------------------ moved edges

    public SceneEdit[] Edits { get { lock (_gate) return _edits.ToArray(); } }

    /// <summary>The user moved the edges of the AI's scene <paramref name="original"/> to <paramref name="edited"/>.</summary>
    public void SetEdit(TimeRange original, TimeRange edited)
    {
        lock (_gate)
        {
            _edits.RemoveAll(e => e.Original.SameScene(original));
            if (edited != original) _edits.Add(new SceneEdit(original, edited));
            _version++;
        }
    }

    /// <summary>Back to the AI's own edges for this scene.</summary>
    public void ClearEdit(TimeRange original)
    {
        lock (_gate)
        {
            _edits.RemoveAll(e => e.Original.SameScene(original));
            _version++;
        }
    }

    private static void AddRange(List<TimeRange> list, TimeRange range)
    {
        list.Add(range);
        list.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        for (var i = list.Count - 1; i > 0; i--)
        {
            if (list[i].StartMs <= list[i - 1].EndMs + 250)
            {
                list[i - 1] = new TimeRange(list[i - 1].StartMs, Math.Max(list[i - 1].EndMs, list[i].EndMs));
                list.RemoveAt(i);
            }
        }
    }

    // ------------------------------------------------------------------ cache

    public static string CacheDirectory => Path.Combine(AppInfo.LocalDirectory, "scans");

    /// <summary>The cache file for this exact file (path + size + time: an edited file is scanned again).</summary>
    private static string? CacheFile(string source)
    {
        try
        {
            var info = new FileInfo(source);
            if (!info.Exists) return null;
            var key = $"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            using var sha = SHA1.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
            return Path.Combine(CacheDirectory, BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant() + ".json");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The cached scan of this file if it was made with the same models, else an empty one.</summary>
    public static SceneScan Load(string source, string modelSet)
    {
        var scan = new SceneScan(source, modelSet);
        var file = CacheFile(source);
        if (file == null || !File.Exists(file)) return scan;

        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(file));
            if (dto == null || dto.Format != FormatVersion) return scan;

            // The user's choices belong to the file, not to the models: keep them for a scan with other models too.
            foreach (var r in dto.Ignored ?? new()) if (r.Length == 2) scan._ignored.Add(new TimeRange(r[0], r[1]));
            foreach (var e in dto.Edits ?? new())
                if (e.Length == 4) scan._edits.Add(new SceneEdit(new TimeRange(e[0], e[1]), new TimeRange(e[2], e[3])));
            if (dto.ModelSet != modelSet) return scan;

            var times = FromBase64Ints(dto.Times);
            var kiss = FromBase64Bytes(dto.Kiss);
            var sex = FromBase64Bytes(dto.Sex);
            var nudeClip = FromBase64Bytes(dto.NudeClip);
            var nudeNet = FromBase64Bytes(dto.NudeNet);
            var change = FromBase64Bytes(dto.Change);
            var n = new[] { times.Length, kiss.Length, sex.Length, nudeClip.Length, nudeNet.Length, change.Length }.Min();
            for (var i = 0; i < n; i++)
                scan._samples.Add(new SceneSample(times[i], kiss[i] / 255f, sex[i] / 255f, nudeClip[i] / 255f, nudeNet[i] / 255f, change[i] / 255f));
            scan._sorted = false;
            foreach (var r in dto.Covered ?? new()) if (r.Length == 2) AddRange(scan._covered, new TimeRange(r[0], r[1]));
            scan._lengthMs = dto.LengthMs;
            scan._device = dto.Device;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Read AI scan " + file);
        }
        return scan;
    }

    public void Save()
    {
        var file = CacheFile(Source);
        if (file == null) return;
        // The scanner (background) and the UI ("don't skip") both save.
        lock (_saveGate) SaveTo(file);
    }

    private readonly object _saveGate = new();

    private void SaveTo(string file)
    {
        try
        {
            var samples = Snapshot();
            Dto dto;
            lock (_gate)
            {
                dto = new Dto
                {
                    Format = FormatVersion,
                    ModelSet = ModelSet,
                    Source = Source,
                    LengthMs = _lengthMs,
                    Device = _device,
                    Saved = DateTime.UtcNow,
                    Covered = _covered.Select(r => new[] { r.StartMs, r.EndMs }).ToList(),
                    Ignored = _ignored.Select(r => new[] { r.StartMs, r.EndMs }).ToList(),
                    Edits = _edits.Select(e => new[] { e.Original.StartMs, e.Original.EndMs, e.Edited.StartMs, e.Edited.EndMs }).ToList(),
                };
            }
            dto.Times = ToBase64(samples.Select(s => (int)Math.Min(int.MaxValue, s.TimeMs)).ToArray());
            dto.Kiss = ToBase64(samples.Select(s => Quantize(s.Kiss)).ToArray());
            dto.Sex = ToBase64(samples.Select(s => Quantize(s.Sex)).ToArray());
            dto.NudeClip = ToBase64(samples.Select(s => Quantize(s.NudeClip)).ToArray());
            dto.NudeNet = ToBase64(samples.Select(s => Quantize(s.NudeNet)).ToArray());
            dto.Change = ToBase64(samples.Select(s => Quantize(s.Change)).ToArray());

            Directory.CreateDirectory(CacheDirectory);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto));
            Compat.MoveOverwrite(tmp, file);
            PruneCache();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Save AI scan " + file);
        }
    }

    private static void PruneCache()
    {
        try
        {
            var files = new DirectoryInfo(CacheDirectory).GetFiles("*.json");
            if (files.Length <= MaxCacheFiles) return;
            foreach (var old in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - MaxCacheFiles))
                old.Delete();
        }
        catch
        {
            // best effort
        }
    }

    private static byte Quantize(float v) => (byte)Math.Round(MathEx.Clamp(v, 0f, 1f) * 255);

    private static string ToBase64(byte[] data) => Convert.ToBase64String(data);

    private static string ToBase64(int[] data)
    {
        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    private static byte[] FromBase64Bytes(string? text) => string.IsNullOrEmpty(text) ? Array.Empty<byte>() : Convert.FromBase64String(text);

    private static int[] FromBase64Ints(string? text)
    {
        var bytes = FromBase64Bytes(text);
        var ints = new int[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, ints, 0, ints.Length * 4);
        return ints;
    }

    private sealed class Dto
    {
        public int Format { get; set; }
        public string ModelSet { get; set; } = "";
        public string Source { get; set; } = "";
        public long LengthMs { get; set; }
        public string? Device { get; set; }
        public DateTime Saved { get; set; }
        public string? Times { get; set; }
        public string? Kiss { get; set; }
        public string? Sex { get; set; }
        public string? NudeClip { get; set; }
        public string? NudeNet { get; set; }
        public string? Change { get; set; }
        public List<long[]>? Covered { get; set; }
        public List<long[]>? Ignored { get; set; }
        /// <summary>Moved edges: AI start, AI end, new start, new end.</summary>
        public List<long[]>? Edits { get; set; }
    }
}
