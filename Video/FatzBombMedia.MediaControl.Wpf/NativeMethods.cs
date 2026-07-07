using System.Runtime.InteropServices;

namespace FatzBombMedia.MediaControl.Wpf;

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

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial IntPtr CreateSolidBrush(int colorRef);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(IntPtr hObject);

#pragma warning disable SYSLIB1054
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);


    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
#pragma warning restore SYSLIB1054

    [StructLayout(LayoutKind.Sequential)]
    internal struct PAINTSTRUCT
    {
        public IntPtr Hdc;
        [MarshalAs(UnmanagedType.Bool)] public bool Erase;
        public RECT RcPaint;
        [MarshalAs(UnmanagedType.Bool)] public bool Restore;
        [MarshalAs(UnmanagedType.Bool)] public bool IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] RgbReserved;
    }

    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TRANSPARENT = 0x00000020;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static partial IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static partial int GetWindowLong32(IntPtr hWnd, int nIndex);

    internal static int GetWindowLong(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? (int)GetWindowLongPtr64(hWnd, nIndex).ToInt64() : GetWindowLong32(hWnd, nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static partial IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static partial int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    internal static int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong) => IntPtr.Size == 8
        ? (int)SetWindowLongPtr64(hWnd, nIndex, new IntPtr(dwNewLong)).ToInt64()
        : SetWindowLong32(hWnd, nIndex, dwNewLong);
}
