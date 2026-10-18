namespace VibePlayer.Core.Ai;

/// <summary>
/// Turns the per-frame scores into scenes to skip:
/// 1. a frame is "hot" when a score passes the sensitivity's threshold, "warm" at 60% of it;
/// 2. a scene grows from hot frames over the warm frames around them, and needs two hot frames
///    (or one very clear one) so a single odd frame doesn't skip anything;
/// 3. scenes close together merge (sex scenes cut away to faces and back);
/// 4. the edges snap to the nearest scene cut, or get a little padding, so nothing flashes by.
/// </summary>
public static class SceneFinder
{
    /// <summary>Picture change between two samples (0..1) that counts as a cut to another shot.</summary>
    public const float CutThreshold = 0.16f;

    private const float WarmFraction = 0.6f;
    private const float ClearPeak = 1.35f;     // one frame this far above the threshold is enough on its own
    private const long SampleGapMs = 3000;     // samples further apart than this aren't neighbours (unscanned part between)
    private const long SnapWindowMs = 4000;    // how far an edge may move to reach a scene cut
    // Padding when there's no cut nearby. More before: a scan that started mid-video can place pictures up
    // to ~0.5 s late (see FrameGrabber), and showing the start of a scene is worse than skipping a bit more.
    private const long StartPadMs = 2000;
    private const long EndPadMs = 1500;
    private const long MinSceneMs = 2000;

    private readonly struct Thresholds
    {
        public Thresholds(float kiss, float sex, float nudeClip, float nudeNet)
        {
            Kiss = kiss;
            Sex = sex;
            NudeClip = nudeClip;
            NudeNet = nudeNet;
        }

        public float Kiss { get; }
        public float Sex { get; }
        public float NudeClip { get; }
        public float NudeNet { get; }
    }

    private static Thresholds For(AiSensitivity sensitivity) => sensitivity switch
    {
        AiSensitivity.Low => new Thresholds(0.70f, 0.65f, 0.75f, 0.60f),
        AiSensitivity.High => new Thresholds(0.35f, 0.30f, 0.45f, 0.32f),
        _ => new Thresholds(0.50f, 0.45f, 0.60f, 0.45f),
    };

    /// <summary>The scenes of the given kinds, in time order, with the user's "don't skip" choices and changed edges applied.</summary>
    public static List<AiScene> Find(IReadOnlyList<SceneSample> samples, SceneKind kinds, AiSensitivity sensitivity,
        IReadOnlyList<TimeRange>? ignored = null, long lengthMs = 0, IReadOnlyList<SceneEdit>? edits = null)
    {
        var t = For(sensitivity);
        var found = new List<AiScene>();
        if (samples.Count == 0) return found;

        if (kinds.HasFlag(SceneKind.Sexual))
        {
            found.AddRange(FindKind(samples, SceneKind.Sexual, 12_000, s =>
                Math.Max(s.Sex / t.Sex, Math.Max(s.NudeClip / t.NudeClip, s.NudeNet / t.NudeNet)), lengthMs));
        }
        if (kinds.HasFlag(SceneKind.Kissing))
            found.AddRange(FindKind(samples, SceneKind.Kissing, 6_000, s => s.Kiss / t.Kiss, lengthMs));

        // Kissing that turns into a sex scene is one scene.
        var merged = new List<AiScene>();
        foreach (var scene in found.OrderBy(s => s.StartMs))
        {
            if (merged.Count > 0 && scene.StartMs <= merged[^1].EndMs + 1000)
            {
                var last = merged[^1];
                last.EndMs = Math.Max(last.EndMs, scene.EndMs);
                last.Kind |= scene.Kind;
                last.Peak = Math.Max(last.Peak, scene.Peak);
            }
            else merged.Add(scene);
        }

        // The user's choices: "don't skip" and changed edges, matched to the AI's own range.
        foreach (var scene in merged)
        {
            scene.OriginalStartMs = scene.StartMs;
            scene.OriginalEndMs = scene.EndMs;
            if (ignored != null) scene.Ignored = ignored.Any(r => r.SameScene(scene.Range));
            if (edits == null) continue;
            foreach (var edit in edits)
            {
                if (!edit.Original.SameScene(scene.OriginalRange)) continue;
                scene.StartMs = Math.Max(0, edit.Edited.StartMs);
                scene.EndMs = lengthMs > 0 ? Math.Min(edit.Edited.EndMs, lengthMs) : edit.Edited.EndMs;
                break;
            }
        }
        return merged;
    }

    private static IEnumerable<AiScene> FindKind(IReadOnlyList<SceneSample> s, SceneKind kind, long mergeGapMs,
        Func<SceneSample, float> level, long lengthMs)
    {
        var n = s.Count;
        var levels = new float[n];
        for (var i = 0; i < n; i++) levels[i] = level(s[i]);

        // 1-2: grow from hot frames over warm neighbours.
        var spans = new List<(int A, int B, float Peak)>();
        for (var i = 0; i < n; i++)
        {
            if (levels[i] < 1f) continue;
            var a = i;
            while (a > 0 && levels[a - 1] >= WarmFraction && s[a].TimeMs - s[a - 1].TimeMs <= SampleGapMs) a--;
            var b = i;
            while (b + 1 < n && levels[b + 1] >= WarmFraction && s[b + 1].TimeMs - s[b].TimeMs <= SampleGapMs) b++;

            int hot = 0;
            var peak = 0f;
            for (var k = a; k <= b; k++)
            {
                if (levels[k] >= 1f) hot++;
                peak = Math.Max(peak, levels[k]);
            }
            if (hot >= 2 || peak >= ClearPeak) spans.Add((a, b, peak));
            i = b;
        }

        // 3: merge spans that are close together.
        var joined = new List<(int A, int B, float Peak)>();
        foreach (var span in spans)
        {
            if (joined.Count > 0 && s[span.A].TimeMs - s[joined[^1].B].TimeMs <= mergeGapMs)
                joined[^1] = (joined[^1].A, span.B, Math.Max(joined[^1].Peak, span.Peak));
            else joined.Add(span);
        }

        // 4: snap the edges to scene cuts, or pad them.
        foreach (var (a, b, peak) in joined)
        {
            var start = s[a].TimeMs - StartPadMs;
            for (var k = a; k > 0 && s[a].TimeMs - s[k].TimeMs <= SnapWindowMs; k--)
            {
                if (s[k].Change < CutThreshold) continue;
                start = s[k - 1].TimeMs;   // the cut is between k-1 and k: start just before it
                break;
            }

            var end = s[b].TimeMs + EndPadMs;
            for (var k = b + 1; k < n && s[k].TimeMs - s[b].TimeMs <= SnapWindowMs; k++)
            {
                if (s[k].Change < CutThreshold) continue;
                end = s[k].TimeMs;         // the first frame of the next shot
                break;
            }

            if (end - start < MinSceneMs)
            {
                var grow = (MinSceneMs - (end - start)) / 2;
                start -= grow;
                end += grow;
            }
            start = Math.Max(0, start);
            if (lengthMs > 0) end = Math.Min(end, lengthMs);
            yield return new AiScene { StartMs = start, EndMs = end, Kind = kind, Peak = peak };
        }
    }
}
