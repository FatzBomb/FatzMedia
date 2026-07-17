#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>
/// Static sink through which engine and platform components can raise host-routable
/// diagnostics. <see cref="MediaControlPlayer"/> subscribes and re-marshals to the
/// public <see cref="IMediaControlSession.MediaDiagnostic"/> event.
/// </summary>
/// <remarks>
/// May fire from background threads (BASS callbacks, decode loops, presentation
/// loop, etc.). Subscribers must marshal to the consuming thread as needed.
/// </remarks>
public static class DiagnosticRouter
{
    /// <summary>
    /// Raised when an engine component emits a host-routable diagnostic. Core owns the
    /// subscription because <see cref="MediaDiagnosticInfo"/> remains an internal transport type.
    /// </summary>
    internal static event Action<MediaDiagnosticInfo>? DiagnosticOccurred;

    public static void Raise(MediaDiagnosticLevel level, string category, string message, string? sourcePath = null, Exception? innerException = null) =>
        DiagnosticOccurred?.Invoke(new MediaDiagnosticInfo(level, category, message, sourcePath, innerException));
}
