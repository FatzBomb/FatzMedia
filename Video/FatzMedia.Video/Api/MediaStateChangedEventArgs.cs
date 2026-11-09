using FatzMedia.Video.Core.Abstractions;

#pragma warning disable IDE0130
namespace FatzMedia.Video;

public sealed class MediaStateChangedEventArgs(
    MediaPlayerState previousState,
    MediaPlayerState currentState) : EventArgs
{
    public MediaPlayerState PreviousState { get; } = previousState;
    public MediaPlayerState CurrentState { get; } = currentState;
}
