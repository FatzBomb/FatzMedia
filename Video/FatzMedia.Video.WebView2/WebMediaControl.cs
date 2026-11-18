using System.ComponentModel;
using System.IO;
using Microsoft.Web.WebView2.Core;
using DockStyle = System.Windows.Forms.DockStyle;
using Keys = System.Windows.Forms.Keys;
using UserControl = System.Windows.Forms.UserControl;
using WinFormsWebView2 = Microsoft.Web.WebView2.WinForms.WebView2;

namespace FatzMedia.Video.WebView2;

/// <summary>
/// WinForms control that hosts a WebView2 and a <see cref="WebView2MediaPlayer"/> (stage 060A); the WinForms
/// counterpart of <see cref="WebMediaView"/>. The web view keeps its cookies in <see cref="UserDataFolder"/>, so a
/// service sign-in survives restarts. The control owns only the web view's lifecycle; playback is <see cref="Player"/>.
/// Full screen, second-screen placement and the double-click event work as on <see cref="WebMediaView"/> (the one web
/// view moves into a borderless window covering a screen, so playback continues; nothing is drawn over YouTube).
/// </summary>
public sealed class WebMediaControl : UserControl, IAsyncDisposable
{
    readonly WinFormsWebView2 _web = new() { Dock = DockStyle.Fill };
    WebView2MediaPlayer? _player;
    Task? _initialize;
    System.Windows.Forms.Form? _fullScreenForm;
    bool _closingFullScreen;
    MouseDoubleClickWatcher? _doubleClickWatcher;

    /// <summary>Creates the control. Set <see cref="UserDataFolder"/> before <see cref="InitializeAsync"/>.</summary>
    public WebMediaControl() => Controls.Add(_web);

    /// <summary>
    /// Gets or sets the folder for the persistent web view profile. Defaults to
    /// <c>%LocalAppData%\FatzMedia\WebView2</c>; hosts should choose their own folder.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string UserDataFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FatzMedia", "WebView2");

    /// <summary>
    /// Gets or sets whether the control goes full screen by itself when the page asks (YouTube's full-screen button),
    /// on the screen its form is on. Default <see langword="true"/>; set <see langword="false"/> and handle
    /// <see cref="FullScreenRequested"/> to decide where the video goes.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool AutoFullScreen { get; set; } = true;

    /// <summary>
    /// Gets or sets whether a double-click on the video toggles the control's own full-screen window, as on YouTube's
    /// site. Default <see langword="true"/>; a host with its own full screen sets it to <see langword="false"/> and
    /// handles <see cref="DoubleClicked"/>.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool DoubleClickTogglesFullScreen { get; set; } = true;

    /// <summary>Gets whether the web view is in its full-screen window.</summary>
    [Browsable(false)]
    public bool IsFullScreen => _fullScreenForm is not null;

    /// <summary>Raised when <see cref="IsFullScreen"/> changes.</summary>
    public event EventHandler? FullScreenChanged;

    /// <summary>Raised when the page asks to enter (<see langword="true"/>) or leave (<see langword="false"/>) full screen.</summary>
    public event EventHandler<bool>? FullScreenRequested;

    /// <summary>Raised on the UI thread when the user double-clicks the web view (the click still reaches the page).</summary>
    public event EventHandler? DoubleClicked;

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
        _doubleClickWatcher = new MouseDoubleClickWatcher(
            WebScreenBounds,
            () => BeginInvoke(OnDoubleClick),
            () => IsFullScreen,
            () => BeginInvoke(ExitFullScreen));
    }

    (int Left, int Top, int Right, int Bottom)? WebScreenBounds()
    {
        if (!_web.Visible || !_web.IsHandleCreated)
            return null;
        var r = _web.RectangleToScreen(_web.ClientRectangle);
        return (r.Left, r.Top, r.Right, r.Bottom);
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

    /// <summary>
    /// Moves the web view into a borderless window that covers <paramref name="screenBounds"/> (a screen's bounds in
    /// device pixels), or the whole screen the control's form is on when it is <see langword="null"/>. Playback
    /// continues. Esc, a double-click or <see cref="ExitFullScreen"/> brings it back.
    /// </summary>
    public void EnterFullScreen(System.Drawing.Rectangle? screenBounds = null)
    {
        if (_fullScreenForm is not null)
            return;

        var pixels = screenBounds
            ?? (FindForm() is { } owner ? System.Windows.Forms.Screen.FromControl(owner) : System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0]).Bounds;
        Controls.Remove(_web);
        var form = new System.Windows.Forms.Form
        {
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
            StartPosition = System.Windows.Forms.FormStartPosition.Manual,
            TopMost = true,
            ShowInTaskbar = false,
            BackColor = System.Drawing.Color.Black,
            KeyPreview = true,
            Bounds = pixels,
        };
        form.Controls.Add(_web);
        form.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
                ExitFullScreen();
        };
        form.FormClosed += OnFullScreenFormClosed;
        _fullScreenForm = form;
        form.Show();
        form.Bounds = pixels;
        FullScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the web view back into this control (Esc does the same while the full-screen window is open).</summary>
    public void ExitFullScreen()
    {
        if (_fullScreenForm is not { } form || _closingFullScreen)
            return;

        _closingFullScreen = true;
        try
        {
            if (_web.CoreWebView2 is { ContainsFullScreenElement: true } core)
                _ = core.ExecuteScriptAsync("document.exitFullscreen && document.exitFullscreen()");
            form.FormClosed -= OnFullScreenFormClosed;
            form.Controls.Remove(_web);
            Controls.Add(_web);
            _fullScreenForm = null;
            form.Close();
        }
        finally
        {
            _closingFullScreen = false;
        }

        FullScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    // The user closed the full-screen window another way (Alt+F4): put the web view back.
    void OnFullScreenFormClosed(object? sender, System.Windows.Forms.FormClosedEventArgs e)
    {
        if (_closingFullScreen || _fullScreenForm is null)
            return;
        _fullScreenForm.Controls.Remove(_web);
        Controls.Add(_web);
        _fullScreenForm = null;
        FullScreenChanged?.Invoke(this, EventArgs.Empty);
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

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        ExitFullScreen();
        _doubleClickWatcher?.Dispose();
        _doubleClickWatcher = null;
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _doubleClickWatcher?.Dispose();
            _doubleClickWatcher = null;
        }

        if (disposing && _player is not null)
        {
            // The player's disposal completes synchronously: it only unhooks the web view's events.
            _player.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _player = null;
        }
        base.Dispose(disposing);
    }
}
