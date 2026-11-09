namespace FatzMedia.Video.Core.Abstractions;

/// <summary>
/// Unified player state across all engines (audio, video, cdg). UI should depend on this only.
/// </summary>
public enum MediaPlayerState
{
    Null,
    Playing,
    Paused,
    Stopped,
    Buffering
}
