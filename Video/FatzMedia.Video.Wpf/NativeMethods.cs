using System.Runtime.InteropServices;

namespace FatzMedia.Video.Wpf;

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    internal const int SW_HIDE = 0;
    internal const int SW_SHOW = 5;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    internal const int BLACK_BRUSH = 4;

    /// <summary>A stock GDI object: shared by the system, never deleted.</summary>
    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr GetStockObject(int index);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);


    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PAINTSTRUCT
    {
        public IntPtr Hdc;
        public int Erase;
        public RECT RcPaint;
        public int Restore;
        public int IncUpdate;
        public RgbReservedBuffer RgbReserved;
        public readonly bool ShouldErase => Erase != 0;
    }

    [StructLayout(LayoutKind.Sequential, Size = 32)]
    internal readonly struct RgbReservedBuffer
    {
        private readonly byte _element0;
    }

    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TRANSPARENT = 0x00000020;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLong32(IntPtr hWnd, int nIndex);

    internal static int GetWindowLong(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? (int)GetWindowLongPtr64(hWnd, nIndex).ToInt64() : GetWindowLong32(hWnd, nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static partial int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    internal static int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong) => IntPtr.Size == 8
        ? (int)SetWindowLongPtr64(hWnd, nIndex, new IntPtr(dwNewLong)).ToInt64()
        : SetWindowLong32(hWnd, nIndex, dwNewLong);
    // Monitor enumeration and placement for SecondaryPresentationWindow (Stage 046A).
    internal const int MONITORINFOF_PRIMARY = 0x00000001;
    internal const int MONITOR_DEFAULTTONEAREST = 0x00000002;
    internal const int MDT_EFFECTIVE_DPI = 0;
    internal const int SWP_NOSIZE = 0x0001;
    internal const int SWP_NOZORDER = 0x0004;
    internal const int SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct MONITORINFOEX
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public int Flags;
        public fixed ushort Device[32];
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr lprcClip,
        delegate* unmanaged<IntPtr, IntPtr, RECT*, IntPtr, int> lpfnEnum,
        IntPtr dwData);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetMonitorInfo(IntPtr hMonitor, MONITORINFOEX* lpmi);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, int uFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}
