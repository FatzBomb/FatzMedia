#pragma warning disable IDE0130
namespace FatzBombMedia.MediaControl;

/// <summary>
/// Severity level for host-routable media-control diagnostics.
/// Hosts can filter on this to decide which diagnostics to surface or log.
/// </summary>
public enum MediaDiagnosticLevel
{
    /// <summary>Informational diagnostic with no impact on playback.</summary>
    Info,

    /// <summary>Non-fatal anomaly that may affect quality or completeness.</summary>
    Warning,

    /// <summary>Failure-level diagnostic indicating a subsystem could not perform a requested action.</summary>
    Error
}
