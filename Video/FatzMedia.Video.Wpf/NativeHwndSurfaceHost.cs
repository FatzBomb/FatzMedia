using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FatzMedia.Video.Wpf;

internal sealed class NativeHwndSurfaceHost(bool startVisible = false) : HwndHost
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
        (var hostWidth, var hostHeight) = GetCurrentClientSize();
        var style = WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
        if (startVisible)
            style |= WS_VISIBLE;

        WindowHandle = NativeMethods.CreateWindowEx(
            0,
            "static",
            string.Empty,
            style,
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

    public void SetNativeVisible(bool isVisible)
    {
        if (WindowHandle == IntPtr.Zero)
            return;

        _ = NativeMethods.ShowWindow(
            WindowHandle,
            isVisible ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero)
            _ = NativeMethods.DestroyWindow(hwnd.Handle);

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
                FillNativeBackground(hwnd, wParam);

            handled = true;
            return new IntPtr(1);
        }

        if (msg == WM_PAINT)
        {
            var hdc = NativeMethods.BeginPaint(hwnd, out NativeMethods.PAINTSTRUCT paint);

            if (hdc != IntPtr.Zero)
                try
                {
                    FillNativeBackground(hwnd, paint.Hdc);
                }
                finally
                {
                    _ = NativeMethods.EndPaint(hwnd, ref paint);
                }

            handled = true;
            return IntPtr.Zero;
        }

        if (msg is WM_SIZE or WM_WINDOWPOSCHANGED)
            QueueBoundsChanged();

        handled = false;
        return IntPtr.Zero;
    }

    private void QueueBoundsChanged()
    {
        if (WindowHandle == IntPtr.Zero || _boundsUpdateQueued)
            return;

        _boundsUpdateQueued = true;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            _boundsUpdateQueued = false;
            (var width, var height) = GetCurrentClientSize();
            if (width == _lastHostWidth && height == _lastHostHeight)
                return;

            _lastHostWidth = width;
            _lastHostHeight = height;
            NativeBoundsChanged?.Invoke(this, EventArgs.Empty);
        }));
    }

    private (int width, int height) GetCurrentClientSize()
    {
        if (WindowHandle != IntPtr.Zero && NativeMethods.GetClientRect(WindowHandle, out NativeMethods.RECT rect))
            return (
                Math.Max(1, rect.Right - rect.Left),
                Math.Max(1, rect.Bottom - rect.Top));

        return (
            Math.Max(1, (int)Math.Round(ActualWidth)),
            Math.Max(1, (int)Math.Round(ActualHeight)));
    }

    private static void FillNativeBackground(IntPtr hwnd, IntPtr hdc)
    {
        if (hwnd == IntPtr.Zero || hdc == IntPtr.Zero || !NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT rect))
            return;

        // The stock black brush: nothing to create or delete per paint (ENG-039).
        var brush = NativeMethods.GetStockObject(NativeMethods.BLACK_BRUSH);
        if (brush != IntPtr.Zero)
            _ = NativeMethods.FillRect(hdc, ref rect, brush);
    }
}
