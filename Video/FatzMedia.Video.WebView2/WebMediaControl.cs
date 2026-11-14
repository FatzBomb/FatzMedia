using System.ComponentModel;
using System.IO;
using Microsoft.Web.WebView2.Core;
using DockStyle = System.Windows.Forms.DockStyle;
using UserControl = System.Windows.Forms.UserControl;
using WinFormsWebView2 = Microsoft.Web.WebView2.WinForms.WebView2;

namespace FatzMedia.Video.WebView2;

/// <summary>
/// WinForms control that hosts a WebView2 and a <see cref="WebView2MediaPlayer"/> (stage 060A); the WinForms
/// counterpart of <see cref="WebMediaView"/>. The web view keeps its cookies in <see cref="UserDataFolder"/>, so a
/// service sign-in survives restarts. The control owns only the web view's lifecycle; playback is <see cref="Player"/>.
/// </summary>
public sealed class WebMediaControl : UserControl, IAsyncDisposable
{
    readonly WinFormsWebView2 _web = new() { Dock = DockStyle.Fill };
    WebView2MediaPlayer? _player;
    Task? _initialize;

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

    /// <summary>Gets the player once <see cref="InitializeAsync"/> has completed.</summary>
    public IWebMediaPlayer Player => _player ?? throw new InvalidOperationException("Call InitializeAsync first.");

    /// <summary>Starts the web view (needs the WebView2 runtime) and creates <see cref="Player"/>.</summary>
    public Task InitializeAsync() => _initialize ??= InitializeCoreAsync();

    async Task InitializeCoreAsync()
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, UserDataFolder, WebView2MediaPlayer.EnvironmentOptions()).ConfigureAwait(true);
        await _web.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
        _player = new WebView2MediaPlayer(_web.CoreWebView2);
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
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && _player is not null)
        {
            // The player's disposal completes synchronously: it only unhooks the web view's events.
            _player.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _player = null;
        }
        base.Dispose(disposing);
    }
}
