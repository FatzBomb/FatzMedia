using System.Windows;
using System.Windows.Interop;

namespace FatzMedia.Video.Wpf;

/// <summary>
/// Full screen for a WPF window: borderless and maximized over the whole screen, taskbar included, and back to the
/// style, state and size it had. The window's maximize and restore animation is turned off from the first switch on (for
/// the window's lifetime), so the picture goes straight to its new size instead of sliding through the animation's
/// in-between sizes (ENG-064). It never creates the window's handle: a window not shown yet is switched once it gets one.
/// <see cref="SecondaryPresentationWindow"/> uses it; a host's own player window can too.
/// </summary>
/// <example>
/// <code>
/// readonly WindowFullScreen _fullScreen = new(this);
/// void ToggleFullScreen() { if (_fullScreen.IsFullScreen) _fullScreen.Exit(); else _fullScreen.Enter(topmost: true); }
/// </code>
/// </example>
/// <param name="window">The window to switch.</param>
public sealed class WindowFullScreen(Window window)
{
    readonly Window _window = window ?? throw new ArgumentNullException(nameof(window));
    WindowStyle _restoreStyle;
    ResizeMode _restoreResizeMode;
    WindowState _restoreState;
    bool _restoreTopmost;
    bool _setTopmost;
    bool _transitionsDisabled;

    /// <summary>Gets whether the window is full screen.</summary>
    public bool IsFullScreen { get; private set; }

    /// <summary>Makes the window cover its screen; <paramref name="topmost"/> keeps it above other windows meanwhile.</summary>
    public void Enter(bool topmost = false)
    {
        if (IsFullScreen)
            return;

        IsFullScreen = true;
        _restoreStyle = _window.WindowStyle;
        _restoreResizeMode = _window.ResizeMode;
        _restoreState = _window.WindowState == WindowState.Minimized ? WindowState.Normal : _window.WindowState;
        _restoreTopmost = _window.Topmost;
        _setTopmost = topmost;

        DisableTransitions();
        _window.WindowStyle = WindowStyle.None;
        _window.ResizeMode = ResizeMode.NoResize;
        if (topmost)
            _window.Topmost = true;

        // Maximizing again after the style change makes the borderless window cover the taskbar too.
        if (_window.WindowState == WindowState.Maximized)
            _window.WindowState = WindowState.Normal;
        _window.WindowState = WindowState.Maximized;
    }

    /// <summary>Gives the window back the style, state and size it had before <see cref="Enter"/>.</summary>
    public void Exit()
    {
        if (!IsFullScreen)
            return;

        IsFullScreen = false;
        // Normal first, so a window maximized before gets its frame back (and the work area) when maximized again.
        _window.WindowState = WindowState.Normal;
        _window.WindowStyle = _restoreStyle;
        _window.ResizeMode = _restoreResizeMode;
        if (_setTopmost)
            _window.Topmost = _restoreTopmost;
        _window.WindowState = _restoreState;
    }

    // Once per window. A window without a handle yet gets it when the handle is made: creating it here would run
    // SourceInitialized early (a SecondaryPresentationWindow would take its session before it is shown). Failing (an
    // old Windows without the attribute) keeps the animation.
    void DisableTransitions()
    {
        if (_transitionsDisabled)
            return;

        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            _window.SourceInitialized -= OnSourceInitialized;
            _window.SourceInitialized += OnSourceInitialized;
            return;
        }

        _transitionsDisabled = true;
        var disabled = 1;
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, ref disabled, sizeof(int));
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _window.SourceInitialized -= OnSourceInitialized;
        DisableTransitions();
    }
}
