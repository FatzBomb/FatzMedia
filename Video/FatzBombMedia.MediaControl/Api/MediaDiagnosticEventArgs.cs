#pragma warning disable IDE0130
namespace FatzBombMedia.MediaControl;

/// <summary>
/// Provides typed diagnostic information for the <see cref="IMediaControlSession.MediaDiagnostic"/> event.
/// </summary>
public sealed class MediaDiagnosticEventArgs(
    MediaDiagnosticLevel level, string category, string message, string? sourcePath = null, Exception? innerException = null)
    : EventArgs
{
    /// <summary>Gets the severity level of the diagnostic.</summary>
    public MediaDiagnosticLevel Level { get; } = level;

    /// <summary>Gets the subsystem or component category that produced this diagnostic.</summary>
    public string Category { get; } = category ?? string.Empty;

    /// <summary>Gets a human-readable description of the diagnostic event.</summary>
    public string Message { get; } = message ?? string.Empty;

    /// <summary>Gets the source path or identifier associated with the diagnostic, if available.</summary>
    public string? SourcePath { get; } = sourcePath;

    /// <summary>Gets the underlying exception that triggered the diagnostic, if captured.</summary>
    public Exception? InnerException { get; } = innerException;
}
