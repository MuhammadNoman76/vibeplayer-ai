using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using VibePlayer.Interop;

namespace VibePlayer.Views;

/// <summary>
/// Transparent, never-activating window that floats exactly over the video area and hosts the
/// YouTube-style controls. (WPF content can't be drawn on top of a native video window directly.)
/// </summary>
public sealed class OverlayWindow : Window
{
    public OverlayWindow(Window owner, FrameworkElement content)
    {
        Owner = owner;
        Title = Core.AppInfo.Name + " controls";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Left = owner.Left;
        Top = owner.Top;
        Width = 1;
        Height = 1;
        Content = content;
    }

    public IntPtr Handle { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Handle = new WindowInteropHelper(this).Handle;
        Native.MakeNonActivating(Handle);
        HwndSource.FromHwnd(Handle)?.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Clicking the controls must never steal activation from the main window (keeps keyboard + title bar active).
        if (msg == Native.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(Native.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }
}
