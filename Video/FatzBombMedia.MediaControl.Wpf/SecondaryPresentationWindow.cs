using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FatzBombMedia.MediaControl.Wpf;

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
        Content = _surfaceHost = new NativeHwndSurfaceHost();

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
        {
            return;
        }

        var pixelWidth = Math.Max(1, _surfaceHost.NativeClientWidth);
        var pixelHeight = Math.Max(1, _surfaceHost.NativeClientHeight);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);

        if (_surfaceAttached)
        {
            _ = _session.ResizePresentationSurface(SurfaceId, pixelWidth, pixelHeight);
            return;
        }

        _surfaceAttached = _session.AttachPresentationSurface(MediaPresentationSurfaceDescriptor.NativeHwnd(
            SurfaceId,
            _role,
            _surfaceHost.WindowHandle,
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

    private sealed class NativeHwndSurfaceHost : HwndHost
    {
        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int WM_PAINT = 0x000F;
        private const int WM_ERASEBKGND = 0x0014;
        private const int WM_SIZE = 0x0005;
        private const int WM_WINDOWPOSCHANGED = 0x0047;

        private int _lastHostWidth;
        private int _lastHostHeight;
        private bool _boundsUpdateQueued;

        public IntPtr WindowHandle { get; private set; }

        public int NativeClientWidth => GetCurrentClientSize().width;

        public int NativeClientHeight => GetCurrentClientSize().height;

        public event EventHandler? NativeHandleChanged;

        public event EventHandler? NativeBoundsChanged;

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            (int hostWidth, int hostHeight) = GetCurrentClientSize();
            WindowHandle = NativeMethods.CreateWindowEx(
                0,
                "static",
                string.Empty,
                WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
                0,
                0,
                hostWidth,
                hostHeight,
                hwndParent.Handle,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            NativeHandleChanged?.Invoke(this, EventArgs.Empty);
            QueueBoundsChanged();
            return new HandleRef(this, WindowHandle);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            if (hwnd.Handle != IntPtr.Zero)
            {
                _ = NativeMethods.DestroyWindow(hwnd.Handle);
            }

            WindowHandle = IntPtr.Zero;
            NativeHandleChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            QueueBoundsChanged();
        }

        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_ERASEBKGND)
            {
                if (wParam != IntPtr.Zero)
                {
                    FillNativeBackground(hwnd, wParam);
                }

                handled = true;
                return new IntPtr(1);
            }

            if (msg == WM_PAINT)
            {
                if (NativeMethods.BeginPaint(hwnd, out NativeMethods.PAINTSTRUCT paint) != IntPtr.Zero)
                {
                    FillNativeBackground(hwnd, paint.Hdc);
                    _ = NativeMethods.EndPaint(hwnd, ref paint);
                }

                handled = true;
                return IntPtr.Zero;
            }

            if (msg is WM_SIZE or WM_WINDOWPOSCHANGED)
            {
                QueueBoundsChanged();
            }

            handled = false;
            return IntPtr.Zero;
        }

        private void QueueBoundsChanged()
        {
            if (WindowHandle == IntPtr.Zero || _boundsUpdateQueued)
            {
                return;
            }

            _boundsUpdateQueued = true;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                _boundsUpdateQueued = false;
                (int width, int height) = GetCurrentClientSize();
                if (width == _lastHostWidth && height == _lastHostHeight)
                {
                    return;
                }

                _lastHostWidth = width;
                _lastHostHeight = height;
                NativeBoundsChanged?.Invoke(this, EventArgs.Empty);
            }));
        }

        private (int width, int height) GetCurrentClientSize()
        {
            if (WindowHandle != IntPtr.Zero && NativeMethods.GetClientRect(WindowHandle, out NativeMethods.RECT rect))
            {
                return (
                    Math.Max(1, rect.Right - rect.Left),
                    Math.Max(1, rect.Bottom - rect.Top));
            }

            return (
                Math.Max(1, (int)Math.Round(ActualWidth)),
                Math.Max(1, (int)Math.Round(ActualHeight)));
        }

        private static void FillNativeBackground(IntPtr hwnd, IntPtr hdc)
        {
            if (hwnd == IntPtr.Zero || hdc == IntPtr.Zero || !NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT rect))
            {
                return;
            }

            IntPtr brush = NativeMethods.CreateSolidBrush(0);
            if (brush == IntPtr.Zero)
            {
                return;
            }

            try
            {
                _ = NativeMethods.FillRect(hdc, ref rect, brush);
            }
            finally
            {
                _ = NativeMethods.DeleteObject(brush);
            }
        }
    }
}
