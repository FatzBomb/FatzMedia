using System.Windows;
using System.Windows.Interop;

namespace FatzMedia.Video.Wpf;

/// <summary>
/// Full screen for a WPF window: borderless and maximized over the whole screen, taskbar included, and back to the
/// style, state and size it had. The window's maximize and restore animation is turned off while it switches, so the
/// picture goes straight to its new size instead of sliding through the animation's in-between sizes (ENG-064).
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
        DisableTransitions();
        // Normal first, so a window maximized before gets its frame back (and the work area) when maximized again.
        _window.WindowState = WindowState.Normal;
        _window.WindowStyle = _restoreStyle;
        _window.ResizeMode = _restoreResizeMode;
        _window.Topmost = _restoreTopmost;
        _window.WindowState = _restoreState;
    }

    // Once the window has a handle; failing (an old Windows without the attribute) keeps the animation.
    void DisableTransitions()
    {
        var hwnd = new WindowInteropHelper(_window).EnsureHandle();
        var disabled = 1;
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, ref disabled, sizeof(int));
    }
}
