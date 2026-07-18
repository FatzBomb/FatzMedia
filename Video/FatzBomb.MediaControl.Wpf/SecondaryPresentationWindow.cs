using System.Windows;
using System.Windows.Media;

namespace FatzBomb.MediaControl.Wpf;

public sealed class SecondaryPresentationWindow : Window
{
    private readonly IMediaControlSession _session;
    private readonly MediaPresentationSurfaceRole _role;
    private readonly NativeHwndSurfaceHost _surfaceHost;
    private bool _surfaceAttached;

    public SecondaryPresentationWindow(
        IMediaControlSession session,
        string surfaceId,
        MediaPresentationSurfaceRole role = MediaPresentationSurfaceRole.Projector)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        SurfaceId = string.IsNullOrWhiteSpace(surfaceId) ? MediaSurface.DefaultSecondarySurfaceId : surfaceId.Trim();
        _role = role;

        Width = 960;
        Height = 540;
        MinWidth = 320;
        MinHeight = 180;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Black;
        Foreground = Brushes.White;
        Content = _surfaceHost = new NativeHwndSurfaceHost(startVisible: true);

        _surfaceHost.NativeHandleChanged += OnNativeSurfaceChanged;
        _surfaceHost.NativeBoundsChanged += OnNativeSurfaceChanged;
        SizeChanged += OnWindowSizeChanged;
        Closed += OnWindowClosed;
    }

    public string SurfaceId { get; }

    private void OnNativeSurfaceChanged(object? sender, EventArgs e) => AttachOrResizeSurface();

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) => AttachOrResizeSurface();

    private void AttachOrResizeSurface()
    {
        if (_surfaceHost.WindowHandle == IntPtr.Zero)
            return;

        var pixelWidth = Math.Max(1, _surfaceHost.NativeClientWidth);
        var pixelHeight = Math.Max(1, _surfaceHost.NativeClientHeight);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);

        if (_surfaceAttached)
        {
            _ = _session.ResizePresentationSurface(SurfaceId, pixelWidth, pixelHeight);
            return;
        }

        _surfaceAttached = _session.AttachPresentationSurface(new MediaPresentationSurfaceDescriptor(
            SurfaceId,
            _role,
            MediaNativeSurfaceDescriptor.Win32Hwnd(_surfaceHost.WindowHandle),
            pixelWidth,
            pixelHeight,
            dpi.DpiScaleX,
            dpi.DpiScaleY));
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        SizeChanged -= OnWindowSizeChanged;
        Closed -= OnWindowClosed;
        _surfaceHost.NativeHandleChanged -= OnNativeSurfaceChanged;
        _surfaceHost.NativeBoundsChanged -= OnNativeSurfaceChanged;

        if (_surfaceAttached)
        {
            _ = _session.DetachPresentationSurface(SurfaceId);
            _surfaceAttached = false;
        }
    }
}
