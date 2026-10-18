namespace VibePlayer.Core;

internal static class Log
{
    private static readonly object Gate = new();

    public static void Error(Exception? ex, string? context = null)
    {
        if (ex == null) return;
        Write("error.log", $"{context}{Environment.NewLine}{ex}{Environment.NewLine}");
    }

    /// <summary>Notes worth keeping for diagnosis (e.g. which chip the AI runs on), in info.log.</summary>
    public static void Info(string message) => Write("info.log", message);

    private static void Write(string fileName, string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.DataDirectory);
                var file = Path.Combine(AppSettings.DataDirectory, fileName);
                if (File.Exists(file) && new FileInfo(file).Length > 1_000_000) File.Delete(file);
                File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the player down.
        }
    }
}
