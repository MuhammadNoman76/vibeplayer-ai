namespace VibePlayer.Core;

/// <summary>
/// What tells VibeVideo AI apart from VibePlayer: its own name, settings folder, processes, pipes, installer
/// and update feed, so both can be installed and run side by side without touching each other's data.
/// </summary>
internal static class AppInfo
{
    /// <summary>Shown to the user.</summary>
    public const string Name = "VibeVideo AI";

    /// <summary>Folders, pipes, mutexes and the process name (VibeVideoAI.exe).</summary>
    public const string Id = "VibeVideoAI";

    /// <summary>VibeVideo AI checks its own feed (VibePlayer's would offer VibePlayer).</summary>
    public static readonly bool UpdatesEnabled = true;

    /// <summary>The update feed on the website (public/updates/vibevideo-ai.json there).</summary>
    public const string UpdateFeedUrl = "https://www.nomanshafiq.com/updates/vibevideo-ai.json";

    /// <summary>The download page, opened when an update can't be installed automatically.</summary>
    public const string DownloadPageUrl = "https://www.nomanshafiq.com/vibevideo-ai/";

    /// <summary>installer\VibeVideoAI.iss AppId: Inno Setup keys the uninstall entry with it.</summary>
    public const string InstallerAppId = "{B7F3D2A1-6C4E-4F8B-9A15-3E2D7C9F0B64}";

    /// <summary>%APPDATA%\VibeVideoAI: settings and the logs.</summary>
    public static string RoamingDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Id);

    /// <summary>%LOCALAPPDATA%\VibeVideoAI: AI models, scan results and the NPU compile cache (large, machine-specific).</summary>
    public static string LocalDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Id);
}
