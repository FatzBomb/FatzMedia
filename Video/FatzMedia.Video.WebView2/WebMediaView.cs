using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Key = System.Windows.Input.Key;
using UserControl = System.Windows.Controls.UserControl;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace FatzMedia.Video.WebView2;

/// <summary>
/// WPF control that hosts a WebView2 and a <see cref="WebView2MediaPlayer"/> (stage 060A). The web view keeps
/// its cookies in <see cref="UserDataFolder"/>, so a service sign-in survives restarts. The control owns only the
/// web view's lifecycle; playback is <see cref="Player"/>.
/// </summary>
/// <remarks>
/// <para><b>Full screen.</b> YouTube's own full-screen request (its button, or a double-click on the video) only
/// fills the web view, so the control moves its one web view into a borderless window that covers a whole screen
/// (<see cref="EnterFullScreen"/>) and back (<see cref="ExitFullScreen"/>). The video keeps playing: it is the same
/// web view, never a second copy. The same calls put it on a second monitor: pass that screen's bounds.</para>
/// <para>Nothing is drawn over YouTube's player, links or ads.</para>
/// </remarks>
public sealed class WebMediaView : UserControl, IAsyncDisposable
{
    readonly WpfWebView2 _web = new();
    WebView2MediaPlayer? _player;
    Task? _initialize;
    Window? _fullScreenWindow;
    MouseDoubleClickWatcher? _doubleClickWatcher;
    bool _closingFullScreen;

    /// <summary>Creates the control. Set <see cref="UserDataFolder"/> before it first loads.</summary>
    public WebMediaView() => Content = _web;

    /// <summary>
    /// Gets or sets the folder for the persistent web view profile. Defaults to
    /// <c>%LocalAppData%\FatzMedia\WebView2</c>; hosts should choose their own folder.
    /// </summary>
    public string UserDataFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FatzMedia", "WebView2");

    /// <summary>
    /// Gets or sets whether the control goes full screen by itself when the page asks (YouTube's full-screen button
    /// or a double-click on the video), on the screen its window is on. Default <see langword="true"/>. Set it to
    /// <see langword="false"/> and handle <see cref="FullScreenRequested"/> to decide where the video goes.
    /// </summary>
    public bool AutoFullScreen { get; set; } = true;

    /// <summary>
    /// Gets or sets whether a double-click on the video toggles the control's own full-screen window, as it does on
    /// YouTube's site. Default <see langword="true"/>. A host that has its own full screen (a player window with
    /// controls) sets it to <see langword="false"/> and handles <see cref="DoubleClicked"/>.
    /// </summary>
    public bool DoubleClickTogglesFullScreen { get; set; } = true;

    /// <summary>
    /// Raised on the UI thread when the user double-clicks the web view. The click still reaches YouTube's player;
    /// the control only observes it (the first click toggles play and pause, the second toggles it back).
    /// </summary>
    public event EventHandler? DoubleClicked;

    /// <summary>Gets whether the web view is in its full-screen window.</summary>
    public bool IsFullScreen => _fullScreenWindow is not null;

    /// <summary>Raised when <see cref="IsFullScreen"/> changes.</summary>
    public event EventHandler? FullScreenChanged;

    /// <summary>
    /// Raised when the page asks to enter (<see langword="true"/>) or leave (<see langword="false"/>) full screen,
    /// before <see cref="AutoFullScreen"/> acts. Raised on the UI thread.
    /// </summary>
    public event EventHandler<bool>? FullScreenRequested;

    /// <summary>Gets the player once <see cref="InitializeAsync"/> has completed.</summary>
    public IWebMediaPlayer Player => _player ?? throw new InvalidOperationException("Call InitializeAsync first.");

    /// <summary>Starts the web view (needs the WebView2 runtime) and creates <see cref="Player"/>.</summary>
    public Task InitializeAsync() => _initialize ??= InitializeCoreAsync();

    async Task InitializeCoreAsync()
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, UserDataFolder, WebView2MediaPlayer.EnvironmentOptions()).ConfigureAwait(true);
        await _web.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
        _player = new WebView2MediaPlayer(_web.CoreWebView2);
        _web.CoreWebView2.ContainsFullScreenElementChanged += OnContainsFullScreenElementChanged;
        _doubleClickWatcher = new MouseDoubleClickWatcher(WebScreenBounds, OnDoubleClick, () => IsFullScreen, () => Dispatcher.BeginInvoke(ExitFullScreen));
    }

    (int Left, int Top, int Right, int Bottom)? WebScreenBounds()
    {
        if (!_web.IsVisible || PresentationSource.FromVisual(_web)?.CompositionTarget is not { } target)
            return null;
        var topLeft = _web.PointToScreen(new System.Windows.Point(0, 0));
        var scale = target.TransformToDevice;
        var width = _web.ActualWidth * scale.M11;
        var height = _web.ActualHeight * scale.M22;
        return ((int)topLeft.X, (int)topLeft.Y, (int)(topLeft.X + width), (int)(topLeft.Y + height));
    }

    void OnDoubleClick()
    {
        // From the mouse hook: do the work after the message has been handled.
        _ = Dispatcher.BeginInvoke(() =>
        {
            DoubleClicked?.Invoke(this, EventArgs.Empty);
            if (!DoubleClickTogglesFullScreen)
                return;
            if (IsFullScreen)
                ExitFullScreen();
            else
                EnterFullScreen();
        });
    }

    /// <summary>Opens an item and (optionally) starts it.</summary>
    public async Task OpenAsync(string address, bool autoplay = false, CancellationToken cancellationToken = default)
    {
        var source = MediaInputSource.Parse(address);
        if (source.WebMedia is not { } item)
            throw new NotSupportedException("Only web media addresses (YouTube) play in the web view.");
        await InitializeAsync().ConfigureAwait(true);
        await Player.LoadAsync(item, autoplay, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Moves the web view into a borderless window that covers <paramref name="bounds"/> (device-independent pixels,
    /// the screen's bounds), or the whole screen the control's window is on when <paramref name="bounds"/> is
    /// <see langword="null"/>. Playback continues. Esc, or <see cref="ExitFullScreen"/>, brings it back.
    /// </summary>
    public void EnterFullScreen(Rect? bounds = null)
    {
        if (_fullScreenWindow is not null)
            return;

        var target = bounds ?? ScreenBoundsOfOwner();
        Content = null;
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Background = System.Windows.Media.Brushes.Black,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = target.Left,
            Top = target.Top,
            Width = target.Width,
            Height = target.Height,
            Content = _web,
        };
        window.KeyDown += OnFullScreenKeyDown;
        window.Closed += OnFullScreenClosed;
        _fullScreenWindow = window;
        window.Show();
        FullScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the web view back into this control (Esc does the same while the full-screen window is open).</summary>
    public void ExitFullScreen()
    {
        if (_fullScreenWindow is not { } window || _closingFullScreen)
            return;

        _closingFullScreen = true;
        try
        {
            // Leave the page's own full-screen state too, so YouTube's button and our window stay in step.
            if (_player is not null && _web.CoreWebView2?.ContainsFullScreenElement == true)
                _ = _web.CoreWebView2.ExecuteScriptAsync("document.fullscreenElement && document.exitFullscreen()");
            window.Content = null;
            Content = _web;
            _fullScreenWindow = null;
            window.KeyDown -= OnFullScreenKeyDown;
            window.Closed -= OnFullScreenClosed;
            window.Close();
        }
        finally
        {
            _closingFullScreen = false;
        }

        FullScreenChanged?.Invoke(this, EventArgs.Empty);
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
        _web.Dispose();
    }

    void OnContainsFullScreenElementChanged(object? sender, object e)
    {
        var wantsFullScreen = _web.CoreWebView2.ContainsFullScreenElement;
        FullScreenRequested?.Invoke(this, wantsFullScreen);
        if (!AutoFullScreen)
            return;

        if (wantsFullScreen)
            EnterFullScreen();
        else
            ExitFullScreen();
    }

    void OnFullScreenKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ExitFullScreen();
        }
    }

    // The user closed the full-screen window another way (Alt+F4): put the web view back.
    void OnFullScreenClosed(object? sender, EventArgs e)
    {
        if (_closingFullScreen || _fullScreenWindow is null)
            return;
        _fullScreenWindow.Content = null;
        Content = _web;
        _fullScreenWindow = null;
        FullScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    Rect ScreenBoundsOfOwner()
    {
        var owner = Window.GetWindow(this);
        var handle = owner is null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(owner).Handle;
        var screen = handle == IntPtr.Zero
            ? System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0]
            : System.Windows.Forms.Screen.FromHandle(handle);
        return ScreenBoundsToDips(screen.Bounds, owner);
    }

    /// <summary>Converts a screen rectangle in device pixels to device-independent pixels for <paramref name="reference"/>.</summary>
    internal static Rect ScreenBoundsToDips(System.Drawing.Rectangle pixels, Visual? reference)
    {
        var source = reference is null ? null : PresentationSource.FromVisual(reference);
        if (source?.CompositionTarget is not { } target)
            return new Rect(pixels.Left, pixels.Top, pixels.Width, pixels.Height);
        var topLeft = target.TransformFromDevice.Transform(new System.Windows.Point(pixels.Left, pixels.Top));
        var bottomRight = target.TransformFromDevice.Transform(new System.Windows.Point(pixels.Right, pixels.Bottom));
        return new Rect(topLeft, bottomRight);
    }
}
