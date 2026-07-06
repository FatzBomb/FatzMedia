#pragma warning disable IDE0130
namespace FatzBombMedia.MediaControl;

/// <summary>
/// Provides typed error information for the <see cref="IMediaControlSession.MediaError"/> event.
/// </summary>
public sealed class MediaErrorEventArgs : EventArgs
{
    /// <summary>Gets the categorized error code.</summary>
    public MediaErrorCode Code { get; }

    /// <summary>Gets a human-readable description of the failure.</summary>
    public string Message { get; }

    /// <summary>Gets the source path or identifier associated with the failure, if available.</summary>
    public string? SourcePath { get; }

    /// <summary>Gets the underlying exception that caused the failure, if captured.</summary>
    public Exception? InnerException { get; }

    public MediaErrorEventArgs(MediaErrorCode code, string message, string? sourcePath = null, Exception? innerException = null)
    {
        Code = code;
        Message = message ?? string.Empty;
        SourcePath = sourcePath;
        InnerException = innerException;
    }
}
