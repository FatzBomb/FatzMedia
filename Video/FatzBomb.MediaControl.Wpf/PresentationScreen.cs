using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FatzBomb.MediaControl.Wpf;

/// <summary>
/// A monitor a <see cref="SecondaryPresentationWindow"/> can be placed on (Stage 046A). WPF has
/// no screen type, so the adapter enumerates monitors itself; bounds are in device pixels.
/// </summary>
public sealed class PresentationScreen
{
    private PresentationScreen(IntPtr handle, string deviceName, Int32Rect bounds, Int32Rect workingArea, bool isPrimary, double scaling)
    {
        Handle = handle;
        DeviceName = deviceName;
        Bounds = bounds;
        WorkingArea = workingArea;
        IsPrimary = isPrimary;
        Scaling = scaling;
    }

    /// <summary>Gets the monitor's device name (for example <c>\\.\DISPLAY2</c>).</summary>
    public string DeviceName { get; }

    /// <summary>Gets the monitor's bounds in device pixels (virtual-screen coordinates).</summary>
    public Int32Rect Bounds { get; }

    /// <summary>Gets the monitor's working area (without the taskbar) in device pixels.</summary>
    public Int32Rect WorkingArea { get; }

    /// <summary>Gets whether this is the primary monitor.</summary>
    public bool IsPrimary { get; }

    /// <summary>Gets the monitor's scale factor (effective DPI / 96).</summary>
    public double Scaling { get; }

    internal IntPtr Handle { get; }

    /// <summary>Gets every monitor, the primary one first.</summary>
    public static IReadOnlyList<PresentationScreen> All
    {
        get
        {
            var handles = new List<IntPtr>();
            GCHandle list = GCHandle.Alloc(handles);
            try
            {
                unsafe
                {
                    _ = NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, &OnMonitor, GCHandle.ToIntPtr(list));
                }
            }
            finally
            {
                list.Free();
            }

            var screens = new List<PresentationScreen>(handles.Count);
            foreach (IntPtr handle in handles)
            {
                if (FromHandle(handle) is { } screen)
                    screens.Add(screen);
            }

            return [.. screens.OrderByDescending(static screen => screen.IsPrimary)];
        }
    }

    /// <summary>Gets the monitor that holds most of <paramref name="window"/>, or null before it has a handle.</summary>
    public static PresentationScreen? FromWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var hwnd = new WindowInteropHelper(window).Handle;
        return hwnd == IntPtr.Zero
            ? null
            : FromHandle(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST));
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PresentationScreen other && other.DeviceName == DeviceName;

    /// <inheritdoc/>
    public override int GetHashCode() => DeviceName.GetHashCode(StringComparison.Ordinal);

    /// <inheritdoc/>
    public override string ToString() => $"{DeviceName} {Bounds.Width}x{Bounds.Height}{(IsPrimary ? " (primary)" : string.Empty)}";

    /// <summary>
    /// The top-left pixel position that centres a window of <paramref name="width"/> x
    /// <paramref name="height"/> pixels in <paramref name="area"/> (clamped to the area).
    /// </summary>
    internal static (int X, int Y) CenteredPosition(Int32Rect area, int width, int height)
    {
        width = Math.Min(area.Width, width);
        height = Math.Min(area.Height, height);
        return (area.X + ((area.Width - width) / 2), area.Y + ((area.Height - height) / 2));
    }

    private static unsafe PresentationScreen? FromHandle(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero)
            return null;

        var info = new NativeMethods.MONITORINFOEX { Size = sizeof(NativeMethods.MONITORINFOEX) };
        if (!NativeMethods.GetMonitorInfo(monitor, &info))
            return null;

        var scaling = NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0
            ? dpiX / 96.0
            : 1.0;
        return new PresentationScreen(
            monitor,
            new string((char*)info.Device),
            ToRect(info.Monitor),
            ToRect(info.Work),
            (info.Flags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
            scaling);
    }

    private static Int32Rect ToRect(NativeMethods.RECT rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    [UnmanagedCallersOnly]
    private static unsafe int OnMonitor(IntPtr monitor, IntPtr hdc, NativeMethods.RECT* clip, IntPtr data)
    {
        if (GCHandle.FromIntPtr(data).Target is List<IntPtr> handles)
            handles.Add(monitor);
        return 1;
    }
}
