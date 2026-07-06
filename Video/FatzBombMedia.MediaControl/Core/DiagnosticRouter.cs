#pragma warning disable IDE0130
namespace FatzBombMedia.MediaControl;

/// <summary>
/// Internal static sink through which any engine component can raise Release-visible
/// diagnostics. <see cref="MediaControlPlayer"/> subscribes and re-marshals to the
/// public <see cref="IMediaControlSession.MediaDiagnostic"/> event.
/// </summary>
/// <remarks>
/// May fire from background threads (BASS callbacks, decode loops, presentation
/// loop, etc.). Subscribers must marshal to the consuming thread as needed.
/// </remarks>
internal static class DiagnosticRouter
{
    /// <summary>
    /// Raised when an engine component emits a host-routable diagnostic.
    /// </summary>
    public static event Action<MediaDiagnosticInfo>? DiagnosticOccurred;

    public static void Raise(MediaDiagnosticLevel level, string category, string message, string? sourcePath = null, Exception? innerException = null)
    {
        DiagnosticOccurred?.Invoke(new MediaDiagnosticInfo(level, category, message, sourcePath, innerException));
    }
}
