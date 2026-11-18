namespace FatzMedia.Video.Sample.Player.Shared.Hosting;

/// <summary>
/// The host's web view for web media (YouTube, Stage 060A): a WebView2 or Avalonia web control that sits in
/// the same area as the media surface. The host owns the control and its player; the view model asks for the
/// player, attaches it to the engine and tells the host when to show the web view.
/// </summary>
public interface IPlayerWebView
{
    /// <summary>
    /// Starts the web view the first time (it needs the WebView2 runtime) and returns its player; later calls
    /// return the same player. Called on the UI thread. Throws when the web view cannot start.
    /// </summary>
    Task<IWebMediaPlayer> EnsureReadyAsync();

    /// <summary>
    /// Shows the web view in the media area (taking over the media surface) or hides it again. Called on the UI
    /// thread; it may be called before <see cref="EnsureReadyAsync"/>.
    /// </summary>
    void SetVisible(bool visible);
}
