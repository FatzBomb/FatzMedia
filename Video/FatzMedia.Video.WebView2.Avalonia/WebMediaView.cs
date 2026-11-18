using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;

namespace FatzMedia.Video.WebView2.Avalonia;

/// <summary>
/// Avalonia control that hosts a <see cref="NativeWebView"/> and an <see cref="AvaloniaWebMediaPlayer"/>
/// (stage 060A); the Avalonia counterpart of the WPF and WinForms web media controls. The control owns only the
/// web view's lifecycle; playback is <see cref="Player"/>. <see cref="EnterFullScreen"/> moves the one web view into a
/// borderless window that covers a screen (also the way to put it on a second monitor); on Windows a double-click on the
/// video raises <see cref="DoubleClicked"/> and Esc leaves full screen (read-only input hooks, nothing is drawn over the
/// player).
/// </summary>
public sealed class WebMediaView : UserControl, IAsyncDisposable
{
    readonly NativeWebView _web = new();
    AvaloniaWebMediaPlayer? _player;
    Window? _fullScreenWindow;
    bool _closingFullScreen;
    MouseDoubleClickWatcher? _doubleClickWatcher;

    /// <summary>Creates the control.</summary>
    public WebMediaView()
    {
        // The host starts playback from its own controls, which the page cannot see as a user gesture: let the web
        // view play without one (WebView2; other platforms keep their defaults).
        _web.EnvironmentRequested += (_, e) =>
        {
            if (e is WindowsWebView2EnvironmentRequestedEventArgs windows)
            {
                windows.AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required";
                windows.UserDataFolder = UserDataFolder;
            }
        };
        Content = _web;
    }

    /// <summary>
    /// Gets or sets whether a double-click on the video toggles the control's own full-screen window (Windows). Default
    /// <see langword="true"/>; a host with its own full screen sets it to <see langword="false"/> and handles
    /// <see cref="DoubleClicked"/>.
    /// </summary>
    public bool DoubleClickTogglesFullScreen { get; set; } = true;

    /// <summary>Raised on the UI thread when the user double-clicks the web view (Windows only; the click still reaches the page).</summary>
    public event EventHandler? DoubleClicked;

    /// <summary>Gets whether the web view is in its full-screen window.</summary>
    public bool IsFullScreen => _fullScreenWindow is not null;

    /// <summary>Raised when <see cref="IsFullScreen"/> changes.</summary>
    public event EventHandler? FullScreenChanged;

    /// <summary>
    /// Moves the web view into a borderless window that covers <paramref name="screenBounds"/> (a screen's bounds in
    /// device pixels), or the whole screen the control's window is on when it is <see langword="null"/>. Playback
    /// continues. A double-click or Esc (Windows), or <see cref="ExitFullScreen"/>, brings it back.
    /// </summary>
    public void EnterFullScreen(PixelRect? screenBounds = null)
    {
        if (_fullScreenWindow is not null)
            return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        var pixels = screenBounds ?? owner?.Screens?.ScreenFromWindow(owner)?.Bounds ?? new PixelRect(0, 0, 1920, 1080);
        var scale = (owner?.Screens?.ScreenFromBounds(pixels)?.Scaling) ?? 1.0;
        var window = new Window
        {
            WindowDecorations = WindowDecorations.None,
            ShowInTaskbar = false,
            Topmost = true,
            Background = global::Avalonia.Media.Brushes.Black,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = pixels.Position,
            Width = pixels.Width / scale,
            Height = pixels.Height / scale,
            CanResize = false,
        };
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                ExitFullScreen();
        };
        window.Closed += OnFullScreenClosed;
        _fullScreenWindow = window;

        // The web view's native window moves with it: reparent without destroying the page.
        using (_web.BeginReparenting())
        {
            Content = null;
            window.Content = _web;
            window.Show();
        }

        window.Position = pixels.Position;
        FullScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the web view back into this control.</summary>
    public void ExitFullScreen()
    {
        if (_fullScreenWindow is not { } window || _closingFullScreen)
            return;

        _closingFullScreen = true;
        try
        {
            window.Closed -= OnFullScreenClosed;
            _fullScreenWindow = null;
            using (_web.BeginReparenting())
            {
                window.Content = null;
                Content = _web;
            }

            window.Close();
        }
        finally
        {
            _closingFullScreen = false;
        }

        FullScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    void OnFullScreenClosed(object? sender, EventArgs e)
    {
        if (_closingFullScreen || _fullScreenWindow is not { } window)
            return;
        _fullScreenWindow = null;
        using (_web.BeginReparenting())
        {
            window.Content = null;
            Content = _web;
        }

        FullScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    // Windows only: the web view runs in another process, so a read-only mouse hook sees the double-click.
    void StartInputWatcher()
    {
        if (!OperatingSystem.IsWindows() || _doubleClickWatcher is not null)
            return;

        _doubleClickWatcher = new MouseDoubleClickWatcher(
            WebScreenBounds,
            () => Dispatcher.UIThread.Post(OnDoubleClick),
            () => IsFullScreen,
            () => Dispatcher.UIThread.Post(ExitFullScreen));
    }

    (int Left, int Top, int Right, int Bottom)? WebScreenBounds()
    {
        if (!_web.IsVisible || TopLevel.GetTopLevel(_web) is not { } top)
            return null;
        var origin = _web.PointToScreen(new Point(0, 0));
        var scale = top.RenderScaling;
        return (origin.X, origin.Y, origin.X + (int)(_web.Bounds.Width * scale), origin.Y + (int)(_web.Bounds.Height * scale));
    }

    void OnDoubleClick()
    {
        DoubleClicked?.Invoke(this, EventArgs.Empty);
        if (!DoubleClickTogglesFullScreen)
            return;
        if (IsFullScreen)
            ExitFullScreen();
        else
            EnterFullScreen();
    }

    /// <summary>
    /// Gets or sets the folder for the persistent web view profile (Windows, WebView2), so a service sign-in
    /// survives restarts. Set it before the control is added to a window. Defaults to
    /// <c>%LocalAppData%\FatzMedia\WebView2.Avalonia</c>; hosts should choose their own folder.
    /// </summary>
    public string UserDataFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FatzMedia", "WebView2.Avalonia");

    /// <summary>
    /// Gets the player, created on first use. Load media only after the control has been added to the visual tree
    /// (the web view creates its native window then).
    /// </summary>
    public IWebMediaPlayer Player
    {
        get
        {
            StartInputWatcher();
            return _player ??= new AvaloniaWebMediaPlayer(_web);
        }
    }

    /// <summary>Opens an item and (optionally) starts it.</summary>
    public async Task OpenAsync(string address, bool autoplay = false, CancellationToken cancellationToken = default)
    {
        var source = MediaInputSource.Parse(address);
        if (source.WebMedia is not { } item)
            throw new NotSupportedException("Only web media addresses (YouTube) play in the web view.");
        await Player.LoadAsync(item, autoplay, cancellationToken).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        ExitFullScreen();
        _doubleClickWatcher?.Dispose();
        _doubleClickWatcher = null;
        if (_player is not null)
            await _player.DisposeAsync().ConfigureAwait(true);
        _player = null;
    }
}
