#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>
/// Provides typed error information for the <see cref="IMediaControlSession.MediaError"/> event.
/// </summary>
public sealed class MediaErrorEventArgs(MediaErrorCode code, string message, string? sourcePath = null, Exception? innerException = null) : EventArgs
{
    /// <summary>Gets the categorized error code.</summary>
    public MediaErrorCode Code { get; } = code;

    /// <summary>Gets a human-readable description of the failure.</summary>
    public string Message { get; } = message ?? string.Empty;

    /// <summary>Gets the source path or identifier associated with the failure, if available.</summary>
    public string? SourcePath { get; } = sourcePath;

    /// <summary>Gets the underlying exception that caused the failure, if captured.</summary>
    public Exception? InnerException { get; } = innerException;
}
