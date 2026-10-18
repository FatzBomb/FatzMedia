using System.Windows;
using System.Windows.Media;

namespace FatzBomb.MediaControl.Wpf;

public sealed class SecondaryPresentationWindow : Window
{
    private readonly IMediaControlSession _session;
    private readonly MediaPresentationSurfaceRole _role;
    private readonly NativeHwndSurfaceHost? _surfaceHost;
    private readonly WpfCompositedSurfaceHost? _compositedHost;
    private bool _surfaceAttached;

    /// <param name="session">The session to present from.</param>
    /// <param name="surfaceId">The secondary surface id.</param>
    /// <param name="role">The surface role.</param>
    /// <param name="presentationMode">
    /// Match the primary surface: a native HWND for swap-chain presentation, or a WPF-composited
    /// frame sink for software mode, which cannot draw into an HWND (ENG-016).
    /// </param>
    public SecondaryPresentationWindow(
        IMediaControlSession session,
        string surfaceId,
        MediaPresentationSurfaceRole role = MediaPresentationSurfaceRole.Projector,
        WpfPresentationMode presentationMode = WpfPresentationMode.NativeHwndOverlay)
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
        if (presentationMode == WpfPresentationMode.WpfComposited)
        {
            Content = _compositedHost = new WpfCompositedSurfaceHost();
            _compositedHost.SizeChanged += OnCompositedHostSizeChanged;
        }
        else
        {
            Content = _surfaceHost = new NativeHwndSurfaceHost(startVisible: true);
            _surfaceHost.NativeHandleChanged += OnNativeSurfaceChanged;
            _surfaceHost.NativeBoundsChanged += OnNativeSurfaceChanged;
            SizeChanged += OnWindowSizeChanged;
        }

        Closed += OnWindowClosed;
    }

    public string SurfaceId { get; }

    private void OnNativeSurfaceChanged(object? sender, EventArgs e) => AttachOrResizeSurface();

    private void OnCompositedHostSizeChanged(object sender, SizeChangedEventArgs e) => AttachOrResizeSurface();

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) => AttachOrResizeSurface();

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        // Moving to a monitor with another scale keeps the DIP size, so no SizeChanged follows:
        // re-render the composited frame at the new pixel size. The HWND host reports its own
        // bounds change.
        if (_compositedHost != null)
            AttachOrResizeSurface();
    }

    private void AttachOrResizeSurface()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int pixelWidth;
        int pixelHeight;
        if (_compositedHost != null)
        {
            if (_compositedHost.ActualWidth <= 0 || _compositedHost.ActualHeight <= 0)
                return;

            pixelWidth = Math.Max(1, (int)Math.Round(_compositedHost.ActualWidth * dpi.DpiScaleX));
            pixelHeight = Math.Max(1, (int)Math.Round(_compositedHost.ActualHeight * dpi.DpiScaleY));
        }
        else
        {
            if (_surfaceHost == null || _surfaceHost.WindowHandle == IntPtr.Zero)
                return;

            pixelWidth = Math.Max(1, _surfaceHost.NativeClientWidth);
            pixelHeight = Math.Max(1, _surfaceHost.NativeClientHeight);
        }

        if (_surfaceAttached)
        {
            _ = _session.ResizePresentationSurface(SurfaceId, pixelWidth, pixelHeight);
            return;
        }

        MediaNativeSurfaceDescriptor nativeSurface;
        if (_compositedHost != null)
            nativeSurface = _compositedHost.CreateSurfaceDescriptor();
        else
            nativeSurface = MediaNativeSurfaceDescriptor.Win32Hwnd(_surfaceHost!.WindowHandle);

        _surfaceAttached = _session.AttachPresentationSurface(new MediaPresentationSurfaceDescriptor(
            SurfaceId,
            _role,
            nativeSurface,
            pixelWidth,
            pixelHeight,
            dpi.DpiScaleX,
            dpi.DpiScaleY));
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        SizeChanged -= OnWindowSizeChanged;
        Closed -= OnWindowClosed;
        if (_surfaceHost != null)
        {
            _surfaceHost.NativeHandleChanged -= OnNativeSurfaceChanged;
            _surfaceHost.NativeBoundsChanged -= OnNativeSurfaceChanged;
        }

        if (_compositedHost != null)
            _compositedHost.SizeChanged -= OnCompositedHostSizeChanged;

        if (_surfaceAttached)
        {
            _ = _session.DetachPresentationSurface(SurfaceId);
            _surfaceAttached = false;
        }

        // Detached first, so later presents skip this window; a present already in flight sees
        // the disposed host and drops its frame.
        _compositedHost?.Dispose();
    }
}
