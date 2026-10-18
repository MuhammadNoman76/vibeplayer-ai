using System.Windows;

namespace VibePlayer.Views;

/// <summary>What the player UI needs from the window that hosts it.</summary>
public interface IPlayerHost
{
    Window HostWindow { get; }
    bool IsFullscreen { get; }
    bool IsMiniPlayer { get; }
    bool AlwaysOnTop { get; set; }

    void ToggleFullscreen();
    void ToggleMiniPlayer();
    void LeaveSpecialModes();
    void SetMediaTitle(string? title);
    void ActivateHost();
    void UpdateTaskbar(bool hasMedia, bool playing, bool hasPrevious, bool hasNext);
}
