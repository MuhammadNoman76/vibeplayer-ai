using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VibePlayer.Views;

/// <summary>One scene in the "Scenes to skip" window: found by the AI (pink) or added by the user (amber).</summary>
public sealed class SceneListItem
{
    /// <summary>Stays the same while the scene is edited (AI scenes: where the AI put them).</summary>
    public string Key { get; set; } = "";
    public bool FromAi { get; set; }
    /// <summary>"Kissing", "Nudity/sex", ... for AI scenes.</summary>
    public string Kind { get; set; } = "";
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    /// <summary>False when the user chose "Don't skip" for an AI scene.</summary>
    public bool Skipped { get; set; } = true;
    /// <summary>An AI scene whose edges the user moved.</summary>
    public bool Edited { get; set; }
    public long OriginalStartMs { get; set; }
    public long OriginalEndMs { get; set; }
}

/// <summary>What the window needs from the player. Each change is applied and saved at once.</summary>
public interface IScenesHost
{
    long LengthMs { get; }
    long TimeMs { get; }
    /// <summary>"Skip scenes the AI finds" is on.</summary>
    bool AiSkipping { get; }
    /// <summary>"Skip marked parts" (your own scenes) is on.</summary>
    bool OwnSkipping { get; }
    IReadOnlyList<SceneListItem> Scenes();
    /// <summary>Moves the edges; returns the scene's key afterwards (your scenes merge with ones they touch).</summary>
    string? SetTimes(SceneListItem scene, long startMs, long endMs);
    void SetSkipped(SceneListItem scene, bool skip);
    void ResetTimes(SceneListItem scene);
    void Remove(SceneListItem scene);
    /// <summary>Adds a scene of your own; returns its key.</summary>
    string? Add(long startMs, long endMs);
    /// <summary>Closes nothing itself: seeks to just before the scene and plays it once without skipping.</summary>
    void Watch(SceneListItem scene);
}

/// <summary>
/// "Scenes to skip": every scene the AI found and every part you marked, with their times and whether they're skipped.
/// Keep or skip a scene, move its start and end (typed, or a second at a time), watch it once, or add one the AI missed.
/// </summary>
public partial class ScenesDialog : Window
{
    private const long Step = 1000;
    private static readonly Color Pink = Color.FromRgb(0xFF, 0x3D, 0x8B);
    private static readonly Color Amber = Color.FromRgb(0xFF, 0xC4, 0x00);

    private readonly IScenesHost _host;
    private List<SceneListItem> _scenes = new();
    private string? _selected;

    public ScenesDialog(IScenesHost host, string? selectKey = null)
    {
        _host = host;
        _selected = selectKey;
        InitializeComponent();

        DoneButton.Click += (_, _) => Close();
        AddButton.Click += (_, _) => AddScene();
        StartEarlier.Click += (_, _) => Nudge(-Step, 0);
        StartLater.Click += (_, _) => Nudge(Step, 0);
        EndEarlier.Click += (_, _) => Nudge(0, -Step);
        EndLater.Click += (_, _) => Nudge(0, Step);
        StartBox.LostKeyboardFocus += (_, _) => CommitTyped();
        EndBox.LostKeyboardFocus += (_, _) => CommitTyped();
        WatchButton.Click += (_, _) =>
        {
            if (Selected is not { } scene) return;
            Close();
            _host.Watch(scene);
        };
        SkipButton.Click += (_, _) =>
        {
            if (Selected is not { } scene) return;
            _host.SetSkipped(scene, !scene.Skipped);
            Refresh();
        };
        ResetButton.Click += (_, _) =>
        {
            if (Selected is not { } scene) return;
            _host.ResetTimes(scene);
            Refresh();
        };
        RemoveButton.Click += (_, _) =>
        {
            if (Selected is not { } scene) return;
            _host.Remove(scene);
            _selected = null;
            Refresh();
        };

        Card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not TextBox) DragMove();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter && e.OriginalSource is TextBox) { CommitTyped(); e.Handled = true; }
        };
        Refresh();
    }

    private SceneListItem? Selected => _scenes.FirstOrDefault(s => s.Key == _selected);

    private void Refresh()
    {
        _scenes = _host.Scenes().OrderBy(s => s.StartMs).ToList();
        if (_selected != null && Selected == null) _selected = null;
        if (_selected == null && _scenes.Count > 0 && _scenes.Count <= 3) _selected = _scenes[0].Key;

        var skipped = _scenes.Where(IsSkipped).ToList();
        var kept = _scenes.Count(s => !s.Skipped);
        var total = skipped.Sum(s => s.EndMs - s.StartMs);
        SummaryText.Text = _scenes.Count == 0
            ? "No scenes yet. When the AI finds one it appears here, or add your own."
            : $"{Count(skipped.Count, "scene")} skipped · {FormatLength(total)} in total" + (kept > 0 ? $" · {kept} kept" : "");

        var notices = new List<string>();
        if (!_host.AiSkipping && _scenes.Any(s => s.FromAi)) notices.Add("“Skip scenes the AI finds” is off, so the pink scenes play.");
        if (!_host.OwnSkipping && _scenes.Any(s => !s.FromAi)) notices.Add("“Skip marked parts” is off, so your amber scenes play.");
        NoticeText.Text = string.Join(" ", notices);
        NoticeText.Visibility = notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        SceneList.Children.Clear();
        foreach (var scene in _scenes) SceneList.Children.Add(Row(scene));
        ShowEditor();
    }

    private bool IsSkipped(SceneListItem s) => s.Skipped && (s.FromAi ? _host.AiSkipping : _host.OwnSkipping);

    private Border Row(SceneListItem scene)
    {
        var selected = scene.Key == _selected;
        var color = scene.FromAi ? Pink : Amber;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Border
        {
            Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(scene.Skipped ? color : Color.FromArgb(0x55, color.R, color.G, color.B)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(dot);
        AddCell(grid, 1, $"{PlayerView.FormatTime(scene.StartMs, _host.LengthMs)} – {PlayerView.FormatTime(scene.EndMs, _host.LengthMs)}", 14, FontWeights.SemiBold, "#F1F1F1");
        AddCell(grid, 2, FormatLength(scene.EndMs - scene.StartMs), 13, FontWeights.Normal, "#AAAAAA");
        var what = !scene.FromAi ? "Added by you" : scene.Edited ? $"{scene.Kind} · AI · times changed" : $"{scene.Kind} · found by the AI";
        AddCell(grid, 3, what, 13, FontWeights.Normal, "#CFCFCF");
        var status = !scene.Skipped ? "Kept" : IsSkipped(scene) ? "Skipped" : "Off";
        var pill = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 3), Margin = new Thickness(8, 0, 4, 0),
            Background = new SolidColorBrush(status == "Skipped" ? Color.FromArgb(0x33, color.R, color.G, color.B) : Color.FromRgb(0x33, 0x33, 0x33)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = status, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(status == "Skipped" ? color : Color.FromRgb(0xAA, 0xAA, 0xAA)),
            },
        };
        Grid.SetColumn(pill, 4);
        grid.Children.Add(pill);

        var row = new Border
        {
            Child = grid, Padding = new Thickness(10, 9, 8, 9), CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand,
            Background = new SolidColorBrush(selected ? Color.FromRgb(0x3A, 0x3A, 0x3A) : Colors.Transparent),
            ToolTip = "Select to change, keep or watch this scene",
        };
        row.MouseEnter += (_, _) => { if (scene.Key != _selected) row.Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C)); };
        row.MouseLeave += (_, _) => { if (scene.Key != _selected) row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, e) =>
        {
            _selected = scene.Key;
            Refresh();
            e.Handled = true;
        };
        row.MouseLeftButtonDown += (_, e) => e.Handled = true; // not a window drag
        return row;
    }

    private static void AddCell(Grid grid, int column, string text, double size, FontWeight weight, string color)
    {
        var block = new TextBlock
        {
            Text = text, FontSize = size, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)new BrushConverter().ConvertFromString(color)!, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    private void ShowEditor()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        if (Selected is not { } scene)
        {
            Editor.Visibility = Visibility.Collapsed;
            return;
        }
        Editor.Visibility = Visibility.Visible;
        EditorTitle.Text = scene.FromAi
            ? $"{scene.Kind}, found by the AI" + (scene.Edited ? $" (it marked {PlayerView.FormatTime(scene.OriginalStartMs, _host.LengthMs)} – {PlayerView.FormatTime(scene.OriginalEndMs, _host.LengthMs)})" : "")
            : "Your scene";
        StartBox.Text = PlayerView.FormatTime(scene.StartMs, _host.LengthMs);
        EndBox.Text = PlayerView.FormatTime(scene.EndMs, _host.LengthMs);
        SkipButton.Visibility = scene.FromAi ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Content = scene.Skipped ? "Don't skip" : "Skip it";
        ResetButton.Visibility = scene.FromAi && scene.Edited ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.Visibility = scene.FromAi ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Nudge(long startBy, long endBy)
    {
        if (Selected is not { } scene) return;
        Apply(scene, scene.StartMs + startBy, scene.EndMs + endBy);
    }

    private void CommitTyped()
    {
        if (Selected is not { } scene || !IsLoaded) return;
        if (!SkipRangeDialog.TryParseTime(StartBox.Text, out var start) || !SkipRangeDialog.TryParseTime(EndBox.Text, out var end))
        {
            ShowError("Enter times like 34:05 or 1:02:30.");
            return;
        }
        if (start == scene.StartMs && end == scene.EndMs) return;
        Apply(scene, start, end);
    }

    private void Apply(SceneListItem scene, long start, long end)
    {
        var length = _host.LengthMs;
        start = Math.Max(0, start);
        if (length > 0) end = Math.Min(end, length);
        if (end - start < 500)
        {
            ShowError("A scene has to be at least half a second long: the end must come after the start.");
            return;
        }
        _selected = _host.SetTimes(scene, start, end) ?? _selected;
        Refresh();
    }

    private void AddScene()
    {
        var length = _host.LengthMs;
        var start = _host.TimeMs;
        if (length > 0 && start >= length - 1000) start = Math.Max(0, length - 30_000);
        var end = length > 0 ? Math.Min(start + 30_000, length) : start + 30_000;
        var key = _host.Add(start, end);
        if (key == null) return;
        _selected = key;
        Refresh();
        StartBox.Focus();
        StartBox.SelectAll();
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    /// <summary>"20 s", "1 min 12 s", "1 h 4 min".</summary>
    public static string FormatLength(long ms)
    {
        var s = (long)Math.Round(ms / 1000.0);
        if (s < 60) return $"{s} s";
        if (s < 3600) return s % 60 == 0 ? $"{s / 60} min" : $"{s / 60} min {s % 60} s";
        return $"{s / 3600} h {s % 3600 / 60} min";
    }
}
