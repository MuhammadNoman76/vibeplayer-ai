using VibePlayer.Interop;

namespace VibePlayer.Core;

public enum MediaKind { Unknown, Video, Audio, Subtitle }

public static class MediaFormats
{
    public static readonly string[] VideoExtensions =
    {
        "mp4", "m4v", "mkv", "webm", "avi", "mov", "qt", "wmv", "flv", "f4v", "mpg", "mpeg", "mpe", "mpv", "m1v", "m2v",
        "m2ts", "mts", "m2t", "ts", "tp", "trp", "vob", "3gp", "3g2", "ogv", "ogm", "divx", "xvid", "asf", "rm", "rmvb",
        "dv", "mxf", "nut", "nsv", "hevc", "h264", "h265", "264", "265", "amv", "y4m", "wtv", "dvr-ms", "mk3d", "evo",
    };

    public static readonly string[] AudioExtensions =
    {
        "mp3", "flac", "wav", "aac", "m4a", "m4b", "ogg", "oga", "opus", "wma", "ape", "alac", "aif", "aiff", "aifc",
        "mka", "ac3", "eac3", "dts", "mpc", "wv", "tta", "amr", "au", "snd", "mp2", "mpa", "spx", "mod", "xm", "s3m", "it", "dsf", "dff",
    };

    public static readonly string[] SubtitleExtensions =
    {
        "srt", "ass", "ssa", "vtt", "sub", "idx", "smi", "sami", "sbv", "ttml", "dfxp", "usf", "jss", "mpl2", "psb", "rt", "stl", "pjs",
    };

    private static readonly HashSet<string> Video = new(VideoExtensions, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Audio = new(AudioExtensions, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Subs = new(SubtitleExtensions, StringComparer.OrdinalIgnoreCase);

    public static MediaKind KindOf(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        if (Video.Contains(ext)) return MediaKind.Video;
        if (Audio.Contains(ext)) return MediaKind.Audio;
        if (Subs.Contains(ext)) return MediaKind.Subtitle;
        return MediaKind.Unknown;
    }

    public static bool IsPlayable(string path)
    {
        var kind = KindOf(path);
        return kind is MediaKind.Video or MediaKind.Audio;
    }

    public static string OpenDialogFilter
    {
        get
        {
            static string Join(IEnumerable<string> exts) => string.Join(";", exts.Select(e => "*." + e));
            var all = VideoExtensions.Concat(AudioExtensions);
            return $"Media files|{Join(all)}|Video files|{Join(VideoExtensions)}|Audio files|{Join(AudioExtensions)}|All files|*.*";
        }
    }

    public static string SubtitleDialogFilter =>
        $"Subtitle files|{string.Join(";", SubtitleExtensions.Select(e => "*." + e))}|All files|*.*";

    public static bool LooksLikeUrl(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)) return false;
        return !uri.IsFile && !string.IsNullOrEmpty(uri.Scheme) && uri.Scheme.Length > 1;
    }

    public static readonly IComparer<string> NaturalComparer =
        Comparer<string>.Create((a, b) => Native.StrCmpLogicalW(a, b));
}
