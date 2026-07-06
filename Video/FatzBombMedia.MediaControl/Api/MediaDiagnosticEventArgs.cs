#pragma warning disable IDE0130
namespace FatzBombMedia.MediaControl;

/// <summary>
/// Provides typed diagnostic information for the <see cref="IMediaControlSession.MediaDiagnostic"/> event.
/// </summary>
public sealed class MediaDiagnosticEventArgs : EventArgs
{
    /// <summary>Gets the severity level of the diagnostic.</summary>
    public MediaDiagnosticLevel Level { get; }

    /// <summary>Gets the subsystem or component category that produced this diagnostic.</summary>
    public string Category { get; }

    /// <summary>Gets a human-readable description of the diagnostic event.</summary>
    public string Message { get; }

    /// <summary>Gets the source path or identifier associated with the diagnostic, if available.</summary>
    public string? SourcePath { get; }

    /// <summary>Gets the underlying exception that triggered the diagnostic, if captured.</summary>
    public Exception? InnerException { get; }

    public MediaDiagnosticEventArgs(MediaDiagnosticLevel level, string category, string message, string? sourcePath = null, Exception? innerException = null)
    {
        Level = level;
        Category = category ?? string.Empty;
        Message = message ?? string.Empty;
        SourcePath = sourcePath;
        InnerException = innerException;
    }
}
