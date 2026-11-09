#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Network buffering state for buffered sources (see <see cref="MediaSourceCapabilities.IsBuffered"/>).
/// </summary>
public sealed class MediaBufferingChangedEventArgs(bool isBuffering, double bufferFill) : EventArgs
{
    /// <summary>Gets whether playback is waiting for data.</summary>
    public bool IsBuffering { get; } = isBuffering;

    /// <summary>Gets the playback buffer fill, <c>0..1</c>.</summary>
    public double BufferFill { get; } = double.IsFinite(bufferFill) ? Math.Clamp(bufferFill, 0, 1) : 0;
}

/// <summary>
/// In-stream metadata from a network source (ICY/Shoutcast or Ogg comments).
/// </summary>
public sealed class MediaStreamMetadataChangedEventArgs(string? title, string? artist, string? rawTitle) : EventArgs
{
    /// <summary>Gets the parsed title, when present.</summary>
    public string? Title { get; } = title;

    /// <summary>Gets the parsed artist, when present.</summary>
    public string? Artist { get; } = artist;

    /// <summary>Gets the unparsed stream title as sent by the server.</summary>
    public string? RawTitle { get; } = rawTitle;
}
