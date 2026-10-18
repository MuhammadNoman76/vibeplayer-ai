namespace VibePlayer.Core;

public sealed class MediaItem
{
    public MediaItem(string source, bool isUrl)
    {
        Source = source;
        IsUrl = isUrl;
    }

    public string Source { get; }
    public bool IsUrl { get; }

    public string DisplayName
    {
        get
        {
            if (!IsUrl) return Path.GetFileNameWithoutExtension(Source);
            if (Uri.TryCreate(Source, UriKind.Absolute, out var uri))
            {
                var last = uri.Segments.LastOrDefault()?.Trim('/');
                if (!string.IsNullOrWhiteSpace(last)) return Uri.UnescapeDataString(last);
                return uri.Host;
            }
            return Source;
        }
    }

    public static MediaItem FromInput(string input) =>
        MediaFormats.LooksLikeUrl(input) ? new MediaItem(input.Trim(), true) : new MediaItem(Path.GetFullPath(input), false);
}

public sealed class Playlist
{
    private const int MaxFolderItems = 5000;

    public Playlist(IEnumerable<MediaItem> items, int index = 0)
    {
        Items = items.ToList();
        Index = Items.Count == 0 ? -1 : MathEx.Clamp(index, 0, Items.Count - 1);
    }

    public static Playlist Empty => new(Array.Empty<MediaItem>());

    public List<MediaItem> Items { get; }
    public int Index { get; private set; }

    public MediaItem? Current => Index >= 0 && Index < Items.Count ? Items[Index] : null;
    public bool HasNext => Index >= 0 && Index < Items.Count - 1;
    public bool HasPrevious => Index > 0;

    public bool MoveNext()
    {
        if (!HasNext) return false;
        Index++;
        return true;
    }

    public bool MovePrevious()
    {
        if (!HasPrevious) return false;
        Index--;
        return true;
    }

    /// <summary>The opened file plus its siblings of the same kind (video or audio), naturally sorted - like YouTube's "up next".</summary>
    public static Playlist FromFolderOf(string file)
    {
        var item = new MediaItem(file, false);
        try
        {
            var dir = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return new Playlist(new[] { item });

            var kind = MediaFormats.KindOf(file);
            var siblings = Directory.EnumerateFiles(dir)
                .Where(f => kind == MediaKind.Unknown ? MediaFormats.IsPlayable(f) : MediaFormats.KindOf(f) == kind)
                .Take(MaxFolderItems)
                .ToList();

            if (!siblings.Contains(file, StringComparer.OrdinalIgnoreCase)) siblings.Add(file);
            siblings.Sort(MediaFormats.NaturalComparer);

            var index = siblings.FindIndex(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase));
            return new Playlist(siblings.Select(f => new MediaItem(f, false)), Math.Max(0, index));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FromFolderOf");
            return new Playlist(new[] { item });
        }
    }

    /// <summary>Every playable file inside a folder (recursively), sorted folder by folder.</summary>
    public static IEnumerable<string> EnumerateFolder(string dir)
    {
        try
        {
            return Compat.EnumerateFilesRecursive(dir)
                .Where(MediaFormats.IsPlayable)
                .Take(MaxFolderItems)
                .OrderBy(f => Path.GetDirectoryName(f) ?? "", MediaFormats.NaturalComparer)
                .ThenBy(f => Path.GetFileName(f), MediaFormats.NaturalComparer)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "EnumerateFolder");
            return Array.Empty<string>();
        }
    }
}
