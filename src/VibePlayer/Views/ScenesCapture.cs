using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VibePlayer.Core;

namespace VibePlayer.Views;

/// <summary>
/// For screenshots and checks without a window: renders the "Scenes to skip" window off-screen, with example scenes for
/// the 2:19 test reel, to a PNG and exits. Nothing is shown on screen.
///   VibeVideoAI.exe --capture-scenes out.png [--select ai|you|kept] [--no-edit]
/// </summary>
internal static class ScenesCapture
{
    public const string Switch = "--capture-scenes";

    public static int Run(string[] args)
    {
        try
        {
            var i = Array.FindIndex(args, a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));
            var output = i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : Path.GetFullPath("scenes.png");
            var s = Array.FindIndex(args, a => string.Equals(a, "--select", StringComparison.OrdinalIgnoreCase));
            var select = s >= 0 && s + 1 < args.Length ? args[s + 1] : "ai";
            var host = new DemoHost(edited: !args.Any(a => string.Equals(a, "--no-edit", StringComparison.OrdinalIgnoreCase)));
            var key = select switch { "you" => "you", "kept" => "ai:15000", _ => "ai:59500" };

            var dialog = new ScenesDialog(host, key);
            var content = (FrameworkElement)dialog.Content;
            dialog.Content = null;
            var root = new System.Windows.Controls.Border { Child = content, Width = dialog.Width };
            // What the window itself would pass down to its content.
            System.Windows.Documents.TextElement.SetForeground(root, dialog.Foreground);
            System.Windows.Documents.TextElement.SetFontFamily(root, dialog.FontFamily);
            root.Measure(new Size(dialog.Width, double.PositiveInfinity));
            root.Arrange(new Rect(root.DesiredSize));
            root.UpdateLayout();

            const double scale = 2;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using (var file = File.Create(output)) encoder.Save(file);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Capture scenes window");
            return 1;
        }
    }

    /// <summary>Example scenes for the test reel: the kiss the AI found (edges moved), a harmless one kept, and one you added.</summary>
    private sealed class DemoHost : IScenesHost
    {
        private readonly List<SceneListItem> _scenes;

        public DemoHost(bool edited)
        {
            _scenes = new List<SceneListItem>
            {
                new() { Key = "ai:15000", FromAi = true, Kind = "Nudity/sex", StartMs = 15000, EndMs = 20500, Skipped = false, OriginalStartMs = 15000, OriginalEndMs = 20500 },
                new()
                {
                    Key = "ai:59500", FromAi = true, Kind = "Kissing", StartMs = edited ? 58500 : 59500, EndMs = edited ? 81500 : 79500,
                    Edited = edited, OriginalStartMs = 59500, OriginalEndMs = 79500,
                },
                new() { Key = "you", Kind = "Added by you", StartMs = 104000, EndMs = 112000, OriginalStartMs = 104000, OriginalEndMs = 112000 },
            };
        }

        public long LengthMs => 139167;
        public long TimeMs => 85000;
        public bool AiSkipping => true;
        public bool OwnSkipping => true;
        public IReadOnlyList<SceneListItem> Scenes() => _scenes;
        public string? SetTimes(SceneListItem scene, long startMs, long endMs) => scene.Key;
        public void SetSkipped(SceneListItem scene, bool skip) { }
        public void ResetTimes(SceneListItem scene) { }
        public void Remove(SceneListItem scene) { }
        public string? Add(long startMs, long endMs) => null;
        public void Watch(SceneListItem scene) { }
    }
}
