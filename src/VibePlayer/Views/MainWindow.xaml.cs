using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using VibePlayer.Core;
using VibePlayer.Interop;

namespace VibePlayer.Views;

public partial class MainWindow : Window, IPlayerHost
{
    private readonly AppSettings _settings;
    private readonly string[] _startupArgs;
    private readonly PlayerView _view;
    private OverlayWindow? _overlay;
    private IntPtr _hwnd;

    private bool _fullscreen;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;

    private bool _mini;
    private Rect _boundsBeforeMini;
    private WindowState _stateBeforeMini = WindowState.Normal;
    private WindowState _lastVisibleState = WindowState.Normal;

    private readonly ThumbButtonInfo _thumbPrevious;
    private readonly ThumbButtonInfo _thumbPlay;
    private readonly ThumbButtonInfo _thumbNext;
    private readonly ImageSource _playImage;
    private readonly ImageSource _pauseImage;

    public MainWindow(AppSettings settings, string[] startupArgs)
    {
        _settings = settings;
        _startupArgs = startupArgs;
        InitializeComponent();
        ApplySavedBounds();
        Topmost = settings.AlwaysOnTop;

        _playImage = MakeThumbImage("Icon.Play");
        _pauseImage = MakeThumbImage("Icon.Pause");
        _thumbPrevious = new ThumbButtonInfo { Description = "Previous", ImageSource = MakeThumbImage("Icon.Previous"), Visibility = Visibility.Collapsed };
        _thumbPlay = new ThumbButtonInfo { Description = "Play", ImageSource = _playImage, Visibility = Visibility.Collapsed };
        _thumbNext = new ThumbButtonInfo { Description = "Next", ImageSource = MakeThumbImage("Icon.Next"), Visibility = Visibility.Collapsed };
        var taskbar = new TaskbarItemInfo();
        taskbar.ThumbButtonInfos.Add(_thumbPrevious);
        taskbar.ThumbButtonInfos.Add(_thumbPlay);
        taskbar.ThumbButtonInfos.Add(_thumbNext);
        TaskbarItemInfo = taskbar;

        _view = new PlayerView(this, settings);
        _thumbPrevious.Click += (_, _) => _view.PlayPrevious();
        _thumbPlay.Click += (_, _) => _view.TogglePlay();
        _thumbNext.Click += (_, _) => _view.PlayNext();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        PreviewKeyDown += (_, e) => _view.HandleKeyDown(e);
    }

    public Window HostWindow => this;
    public bool IsFullscreen => _fullscreen;
    public bool IsMiniPlayer => _mini;

    public bool AlwaysOnTop
    {
        get => _settings.AlwaysOnTop;
        set
        {
            _settings.AlwaysOnTop = value;
            if (!_mini) Topmost = value;
        }
    }

    // ------------------------------------------------------------------ lifecycle

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.ApplyDarkTitleBar(_hwnd);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _overlay = new OverlayWindow(this, _view);
        _overlay.Show();
        SyncOverlay();
        Keyboard.Focus(this);
        _view.Start(Video.SurfaceHandle, _startupArgs);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _view.Shutdown();

        if (_fullscreen || _mini)
        {
            // Remember the normal window, not the temporary fullscreen / mini bounds.
            var bounds = _mini ? _boundsBeforeMini : RestoreBounds;
            SaveBounds(bounds, (_mini ? _stateBeforeMini : _stateBeforeFullscreen) == WindowState.Maximized);
        }
        else
        {
            SaveBounds(WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds,
                WindowState == WindowState.Maximized);
        }
        _settings.Save();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) _lastVisibleState = WindowState;
        SyncOverlay();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is Native.WM_WINDOWPOSCHANGED or Native.WM_SIZE or Native.WM_MOVE)
            SyncOverlay();
        return IntPtr.Zero;
    }

    /// <summary>Keeps the controls overlay glued to the client area (in physical pixels).</summary>
    private void SyncOverlay()
    {
        if (_overlay == null || _overlay.Handle == IntPtr.Zero || _hwnd == IntPtr.Zero) return;
        if (Native.IsIconic(_hwnd)) return;

        Native.GetClientRect(_hwnd, out var rc);
        var origin = new Native.POINT();
        Native.ClientToScreen(_hwnd, ref origin);
        Native.SetWindowPos(_overlay.Handle, IntPtr.Zero, origin.X, origin.Y,
            Math.Max(1, rc.Right - rc.Left), Math.Max(1, rc.Bottom - rc.Top),
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    // ------------------------------------------------------------------ window modes

    public void ToggleFullscreen()
    {
        if (_mini) ExitMini();
        if (_fullscreen) ExitFullscreen();
        else EnterFullscreen();
        _view.OnWindowModeChanged();
    }

    private void EnterFullscreen()
    {
        _stateBeforeFullscreen = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        _fullscreen = true;
        // Maximize a borderless window: it then covers the taskbar too.
        if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        Activate();
    }

    private void ExitFullscreen()
    {
        _fullscreen = false;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        WindowState = _stateBeforeFullscreen;
    }

    public void ToggleMiniPlayer()
    {
        if (_fullscreen) ExitFullscreen();
        if (_mini) ExitMini();
        else EnterMini();
        _view.OnWindowModeChanged();
    }

    private void EnterMini()
    {
        _stateBeforeMini = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        _boundsBeforeMini = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _mini = true;

        WindowState = WindowState.Normal;
        var work = GetWorkAreaDip();
        Width = 480;
        Height = 302;
        Left = work.Right - Width - 16;
        Top = work.Bottom - Height - 16;
        Topmost = true;
    }

    private void ExitMini()
    {
        _mini = false;
        Topmost = _settings.AlwaysOnTop;
        if (!_boundsBeforeMini.IsEmpty)
        {
            Left = _boundsBeforeMini.Left;
            Top = _boundsBeforeMini.Top;
            Width = _boundsBeforeMini.Width;
            Height = _boundsBeforeMini.Height;
        }
        WindowState = _stateBeforeMini;
    }

    public void LeaveSpecialModes()
    {
        if (_fullscreen) ToggleFullscreen();
        else if (_mini) ToggleMiniPlayer();
    }

    private Rect GetWorkAreaDip()
    {
        var work = Native.GetWorkArea(_hwnd);
        var source = PresentationSource.FromVisual(this);
        var m = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = m.Transform(new Point(work.Left, work.Top));
        var bottomRight = m.Transform(new Point(work.Right, work.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    // ------------------------------------------------------------------ host services

    public void SetMediaTitle(string? title) =>
        Title = string.IsNullOrWhiteSpace(title) ? AppInfo.Name : $"{title} - {AppInfo.Name}";

    public void ActivateHost()
    {
        if (WindowState == WindowState.Minimized) WindowState = _lastVisibleState;
        if (!IsActive)
        {
            Activate();
            Native.SetForegroundWindow(_hwnd);
        }
        Keyboard.Focus(this);
    }

    public void OpenFromAnotherInstance(string[] args)
    {
        ActivateHost();
        if (args.Length > 0) _view.OpenFromExternal(args);
    }

    public void UpdateTaskbar(bool hasMedia, bool playing, bool hasPrevious, bool hasNext)
    {
        _thumbPlay.Visibility = hasMedia ? Visibility.Visible : Visibility.Collapsed;
        _thumbPlay.ImageSource = playing ? _pauseImage : _playImage;
        _thumbPlay.Description = playing ? "Pause" : "Play";
        _thumbPrevious.Visibility = hasMedia ? Visibility.Visible : Visibility.Collapsed;
        _thumbPrevious.IsEnabled = hasPrevious;
        _thumbNext.Visibility = hasMedia ? Visibility.Visible : Visibility.Collapsed;
        _thumbNext.IsEnabled = hasNext;
    }

    private static ImageSource MakeThumbImage(string iconKey)
    {
        var geometry = (Geometry)Application.Current.FindResource(iconKey);
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        group.Children.Add(new GeometryDrawing(Brushes.White, null, geometry));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    // ------------------------------------------------------------------ bounds persistence

    private void ApplySavedBounds()
    {
        if (_settings.WindowWidth is not { } w || _settings.WindowHeight is not { } h ||
            _settings.WindowLeft is not { } left || _settings.WindowTop is not { } top)
            return;

        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var bounds = new Rect(left, top, Math.Max(MinWidth, w), Math.Max(MinHeight, h));
        if (!virtualScreen.IntersectsWith(bounds)) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveBounds(Rect bounds, bool maximized)
    {
        if (bounds.IsEmpty || bounds.Width < 100 || bounds.Height < 100) return;
        _settings.WindowLeft = bounds.Left;
        _settings.WindowTop = bounds.Top;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        _settings.WindowMaximized = maximized;
    }
}
