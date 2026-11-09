#pragma warning disable IDE0130
namespace FatzMedia.Video;

public sealed class MediaSeekCompletedEventArgs(
    TimeSpan requestedPosition,
    TimeSpan resultingPosition) : EventArgs
{
    public TimeSpan RequestedPosition { get; } = requestedPosition;
    public TimeSpan ResultingPosition { get; } = resultingPosition;
}
