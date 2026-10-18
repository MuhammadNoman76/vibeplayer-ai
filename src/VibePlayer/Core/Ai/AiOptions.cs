using System.Text.Json.Serialization;

namespace VibePlayer.Core.Ai;

/// <summary>How sure the AI must be before it skips a scene. High catches more and also skips more by mistake.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiSensitivity { Low, Medium, High }

/// <summary>Which chip runs the AI models. Auto: the NPU, else the best GPU, else the CPU.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiProcessor { Auto, Npu, Gpu, Cpu }

/// <summary>What a found scene contains.</summary>
[Flags]
public enum SceneKind
{
    None = 0,
    Sexual = 1,   // nudity or sex
    Kissing = 2,  // kissing or intimate closeness
}

/// <summary>What the models saw in one sampled frame. Scores are 0..1.</summary>
public readonly record struct SceneSample(
    long TimeMs,
    float Kiss,       // CLIP: kissing / intimate
    float Sex,        // CLIP: sex scene
    float NudeClip,   // CLIP: nudity
    float NudeNet,    // NudeNet: strongest exposed-body-part detection (0 when NudeNet is off)
    float Change);    // how different the picture is from the previous sample (finds scene cuts)

/// <summary>A span of the video, in milliseconds.</summary>
public readonly record struct TimeRange(long StartMs, long EndMs)
{
    public long Length => EndMs - StartMs;

    public long Overlap(TimeRange other) =>
        Math.Max(0, Math.Min(EndMs, other.EndMs) - Math.Max(StartMs, other.StartMs));

    /// <summary>
    /// Is this the same scene as <paramref name="other"/>? They overlap by at least half of the shorter one. Used to
    /// match the user's choices to scenes again after a rescan or a sensitivity change moved the edges a little.
    /// </summary>
    public bool SameScene(TimeRange other) => Overlap(other) * 2 >= Math.Min(Length, other.Length);
}

/// <summary>The user moved the edges of a scene the AI found: <see cref="Original"/> is where the AI put it.</summary>
public readonly record struct SceneEdit(TimeRange Original, TimeRange Edited);

/// <summary>A scene the AI found.</summary>
public sealed class AiScene
{
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    public SceneKind Kind { get; set; }
    /// <summary>How far above the threshold the strongest frame was (1 = just at it).</summary>
    public float Peak { get; set; }
    /// <summary>The user said "don't skip this one".</summary>
    public bool Ignored { get; set; }
    /// <summary>Where the AI put the scene, before any change by the user.</summary>
    public long OriginalStartMs { get; set; }
    public long OriginalEndMs { get; set; }
    /// <summary>The user changed the start or the end.</summary>
    public bool Edited => StartMs != OriginalStartMs || EndMs != OriginalEndMs;

    public TimeRange Range => new(StartMs, EndMs);
    public TimeRange OriginalRange => new(OriginalStartMs, OriginalEndMs);

    public string KindText => Kind switch
    {
        SceneKind.Sexual | SceneKind.Kissing => "Kissing & nudity/sex",
        SceneKind.Sexual => "Nudity/sex",
        SceneKind.Kissing => "Kissing",
        _ => "Scene",
    };
}
