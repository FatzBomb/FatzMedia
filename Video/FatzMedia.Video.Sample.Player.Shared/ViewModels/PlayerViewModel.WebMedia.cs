using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FatzMedia.Video.Sample.Player.Shared.ViewModels;

/// <summary>
/// Web links for the test players (Stage 060A): a YouTube address typed into the Next flyout loads through
/// <see cref="PlayerCore.LoadAsync(string)"/> and plays in the host's web view, which takes over the media area
/// while the item lasts. Sign in / Sign out open or clear the web view's own profile. The host supplies the web
/// view (<see cref="Hosting.IPlayerWebView"/>); the view model attaches its player to the engine and shows or hides
/// the view, so the hosts keep view code only.
/// </summary>
public partial class PlayerViewModel
{
    IWebMediaPlayer? _attachedWebPlayer;

    /// <summary>Loads the address in <see cref="WebLink"/> (a YouTube video) and plays it in the web view.</summary>
    public AsyncRelayCommand OpenWebLinkCommand { get; private set; } = null!;

    /// <summary>Shows the service's own sign-in page in the web view (the profile keeps the sign-in).</summary>
    public AsyncRelayCommand WebSignInCommand { get; private set; } = null!;

    /// <summary>Clears the web view's profile (cookies and storage), signing out of every service.</summary>
    public AsyncRelayCommand WebSignOutCommand { get; private set; } = null!;

    /// <summary>Gets whether the host has a web view, so the web link controls apply.</summary>
    public bool HasWebView => _host.WebView is not null;

    /// <summary>Gets or sets the address typed into the web link box.</summary>
    public string WebLink
    {
        get;
        set
        {
            if (SetProperty(ref field, value ?? string.Empty))
                OpenWebLinkCommand.NotifyCanExecuteChanged();
        }
    } = string.Empty;

    /// <summary>Gets a short line about the web view: what happened to the last link, sign-in, sign-out.</summary>
    [ObservableProperty]
    public partial string WebInfoText { get; private set; } = string.Empty;

    void InitializeWebMediaControls()
    {
        OpenWebLinkCommand = new AsyncRelayCommand(OpenWebLinkAsync, () => CanUsePlayer() && HasWebView && WebLink.Trim().Length > 0);
        WebSignInCommand = new AsyncRelayCommand(WebSignInAsync, () => !IsBusy && HasWebView);
        WebSignOutCommand = new AsyncRelayCommand(WebSignOutAsync, () => !IsBusy && HasWebView);
    }

    void RefreshWebMediaCommandStates()
    {
        OpenWebLinkCommand.NotifyCanExecuteChanged();
        WebSignInCommand.NotifyCanExecuteChanged();
        WebSignOutCommand.NotifyCanExecuteChanged();
    }

    void StartWebMediaTracking(PlayerCore player)
    {
        player.MediaLoaded += OnWebTrackingMediaChanged;
        player.MediaClosed += OnWebTrackingMediaChanged;
    }

    void StopWebMediaTracking(PlayerCore player)
    {
        player.MediaLoaded -= OnWebTrackingMediaChanged;
        player.MediaClosed -= OnWebTrackingMediaChanged;
        // The host owns the web player; the engine just stops using it.
        if (_attachedWebPlayer is not null)
        {
            player.AttachWebMediaPlayer(null);
            _attachedWebPlayer = null;
        }
    }

    // A local file (or a close) ends the web item: the web view gives the media area back to the engine surface.
    void OnWebTrackingMediaChanged(object? sender, EventArgs e) => RunOnUiThread(() =>
    {
        if (sender is PlayerCore player && !player.IsWebMedia)
            _host.WebView?.SetVisible(false);
    });

    async Task OpenWebLinkAsync()
    {
        if (Player is not { } player || _host.WebView is not { } web)
            return;

        var link = WebLink.Trim();
        var outcome = WebMediaProviders.Parse(link).Outcome;
        if (outcome == WebMediaParseOutcome.NotWebMedia)
        {
            WebInfoText = "Not a YouTube link.";
            StatusText = "Enter a YouTube video address (youtube.com or youtu.be).";
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = $"Opening web link: {link}";
            WebInfoText = "Opening...";
            if (outcome == WebMediaParseOutcome.Recognized)
            {
                // The web view starts and joins the engine first; then the load reaches it through Player.LoadAsync.
                var webPlayer = await web.EnsureReadyAsync();
                AttachWebPlayer(player, webPlayer);
                web.SetVisible(true);
            }

            LoadingMediaText = link;
            var loaded = await player.LoadAsync(link);
            if (loaded)
                LoadedMediaText = link;
            else if (!player.IsWebMedia)
                web.SetVisible(false);
            RefreshPlaybackPosition();
            RunOnUiThread(RefreshEffectValues);
            WebInfoText = loaded ? "Web video loaded." : "Could not load the link.";
            StatusText = loaded ? $"Loaded web link: {link}" : $"Could not load web link: {link}";
        }
        catch (Exception ex)
        {
            if (!player.IsWebMedia)
                web.SetVisible(false);
            WebInfoText = "Could not open the web view.";
            StatusText = $"Web link failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    void AttachWebPlayer(PlayerCore player, IWebMediaPlayer webPlayer)
    {
        if (ReferenceEquals(_attachedWebPlayer, webPlayer))
            return;

        player.AttachWebMediaPlayer(webPlayer);
        _attachedWebPlayer = webPlayer;
    }

    async Task WebSignInAsync()
    {
        if (_host.WebView is not { } web)
            return;

        try
        {
            IsBusy = true;
            WebInfoText = "Opening the sign-in page...";
            var webPlayer = await web.EnsureReadyAsync();
            web.SetVisible(true);
            await webPlayer.SignInAsync();
            WebInfoText = "Sign in on the page, then press Open.";
            StatusText = "Web view: sign in on Google's page, then open the link again.";
        }
        catch (Exception ex)
        {
            web.SetVisible(false);
            WebInfoText = "Could not open the sign-in page.";
            StatusText = $"Web sign-in failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    async Task WebSignOutAsync()
    {
        if (_host.WebView is not { } web)
            return;

        try
        {
            IsBusy = true;
            var webPlayer = await web.EnsureReadyAsync();
            await webPlayer.SignOutAsync();
            WebInfoText = "Signed out (web view profile cleared).";
            StatusText = "Web view: signed out of every service.";
        }
        catch (Exception ex)
        {
            WebInfoText = "Could not sign out.";
            StatusText = $"Web sign-out failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
