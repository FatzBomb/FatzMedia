using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FatzMedia.Video.WebView2;

/// <summary>
/// Sees a double-click on the web view from the host side. The web view runs in its own browser process and takes the
/// mouse there, so the WPF tree never hears a click on the video, and the page does not report a double-click on
/// YouTube's player (it is in another origin). A low-level mouse hook watches the system's mouse input instead: it
/// reads the left-button presses (and the Esc key, only to leave the full-screen window) and changes nothing, so the video, YouTube's player, its links and its ads are not
/// covered, blocked or altered. It runs on the UI thread's message loop and only compares a point with the web view's
/// rectangle.
/// </summary>
sealed unsafe partial class MouseDoubleClickWatcher : IDisposable
{
    const int WhKeyboardLowLevel = 13;
    const int WhMouseLowLevel = 14;
    const int WmKeyDown = 0x0100;
    const uint VkEscape = 0x1B;
    const int WmLeftButtonDown = 0x0201;
    const int SmCxDoubleClick = 36;
    const int SmCyDoubleClick = 37;

    static readonly List<MouseDoubleClickWatcher> Active = [];
    static readonly Lock ActiveGate = new();

    readonly Func<(int Left, int Top, int Right, int Bottom)?> _bounds;
    readonly Action _doubleClicked;
    static nint _mouseHook;
    static nint _keyHook;
    readonly Func<bool>? _escapeActive;
    readonly Action? _escape;
    readonly DoubleClickDetector _detector = new();

    /// <summary>Watches the calling (UI) thread. <paramref name="bounds"/> gives the web view's rectangle in screen pixels.</summary>
    public MouseDoubleClickWatcher(
        Func<(int Left, int Top, int Right, int Bottom)?> bounds,
        Action doubleClicked,
        Func<bool>? escapeActive = null,
        Action? escape = null)
    {
        _bounds = bounds;
        _doubleClicked = doubleClicked;
        _escapeActive = escapeActive;
        _escape = escape;
        // One hook of each kind is shared by every watcher; one per watcher would run each press through every
        // watcher once per hook, and the detector would see the same press twice and call it a double-click.
        lock (ActiveGate)
        {
            Active.Add(this);
            if (_mouseHook == 0)
                _mouseHook = SetWindowsHookExW(WhMouseLowLevel, &HookCallback, GetModuleHandleW(null), 0);
            if (escape is not null && _keyHook == 0)
                _keyHook = SetWindowsHookExW(WhKeyboardLowLevel, &KeyHookCallback, GetModuleHandleW(null), 0);
        }
    }

    public void Dispose()
    {
        lock (ActiveGate)
        {
            Active.Remove(this);
            if (Active.Count == 0 && _mouseHook != 0)
            {
                _ = UnhookWindowsHookEx(_mouseHook);
                _mouseHook = 0;
            }

            if (_keyHook != 0 && !Active.Exists(w => w._escape is not null))
            {
                _ = UnhookWindowsHookEx(_keyHook);
                _keyHook = 0;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static nint HookCallback(int code, nint wParam, nint lParam)
    {
        try
        {
            if (code >= 0 && wParam == WmLeftButtonDown)
            {
                var info = *(LowLevelMouseStruct*)lParam;
                MouseDoubleClickWatcher[] watchers;
                lock (ActiveGate)
                    watchers = [.. Active];
                foreach (var watcher in watchers)
                    watcher.OnLeftButtonDown(info.X, info.Y, info.Time);
            }
        }
        catch (Exception)
        {
            // An exception must not leave an unmanaged callback (it would end the process); a missed click is harmless.
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static nint KeyHookCallback(int code, nint wParam, nint lParam)
    {
        try
        {
            if (code >= 0 && wParam == WmKeyDown && ((LowLevelKeyStruct*)lParam)->VkCode == VkEscape)
            {
                MouseDoubleClickWatcher[] watchers;
                lock (ActiveGate)
                    watchers = [.. Active];
                foreach (var watcher in watchers)
                {
                    if (watcher._escapeActive?.Invoke() == true)
                        watcher._escape?.Invoke();
                }
            }
        }
        catch (Exception)
        {
            // See HookCallback.
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    void OnLeftButtonDown(int x, int y, uint time)
    {
        if (_bounds() is not { } b || x < b.Left || x >= b.Right || y < b.Top || y >= b.Bottom)
        {
            _detector.Reset();
            return;
        }

        if (_detector.Press(x, y, time, GetDoubleClickTime(), GetSystemMetrics(SmCxDoubleClick) / 2, GetSystemMetrics(SmCyDoubleClick) / 2))
            _doubleClicked();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LowLevelMouseStruct
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    /// <summary>Puts a window at an exact pixel rectangle, topmost, without taking focus.</summary>
    public static void PlaceWindow(nint hwnd, int x, int y, int width, int height) =>
        _ = SetWindowPos(hwnd, -1, x, y, width, height, 0x0010 /* SWP_NOACTIVATE */);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    struct LowLevelKeyStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowsHookExW(int idHook, delegate* unmanaged[Stdcall]<int, nint, nint, nint> lpfn, nint hMod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial uint GetDoubleClickTime();

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? lpModuleName);
}
