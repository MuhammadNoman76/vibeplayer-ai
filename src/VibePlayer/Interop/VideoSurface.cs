using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace VibePlayer.Interop;

/// <summary>
/// A plain black Win32 child window that LibVLC renders video into (native Direct3D output,
/// exactly like VLC itself). The WPF controls live in a transparent overlay window on top.
/// </summary>
public sealed class VideoSurface : HwndHost
{
    private const string ClassName = "VibePlayerVideoSurface";
    private static bool s_registered;

    public IntPtr SurfaceHandle { get; private set; }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureClassRegistered();
        SurfaceHandle = Native.CreateWindowEx(0, ClassName, "",
            Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPCHILDREN | Native.WS_CLIPSIBLINGS,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);

        if (SurfaceHandle == IntPtr.Zero)
            throw new InvalidOperationException("Could not create the video surface (error " + Marshal.GetLastWin32Error() + ").");

        return new HandleRef(this, SurfaceHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Native.DestroyWindow(hwnd.Handle);
        SurfaceHandle = IntPtr.Zero;
    }

    private static void EnsureClassRegistered()
    {
        if (s_registered) return;

        // The surface never handles messages itself, so the class can point straight at DefWindowProcW.
        var user32 = Native.GetModuleHandle("user32.dll");
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Native.GetProcAddress(user32, "DefWindowProcW"),
            hInstance = Native.GetModuleHandle(null),
            hCursor = Native.LoadCursor(IntPtr.Zero, new IntPtr(32512) /* IDC_ARROW */),
            hbrBackground = Native.GetStockObject(4 /* BLACK_BRUSH */),
            lpszClassName = ClassName,
        };
        Native.RegisterClassEx(ref wc);
        s_registered = true;
    }
}
