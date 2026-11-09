#pragma warning disable IDE0130
namespace FatzMedia.Video;

public sealed class MediaPositionChangedEventArgs(TimeSpan position, TimeSpan duration) : EventArgs
{
    public TimeSpan Position { get; } = position;
    public TimeSpan Duration { get; } = duration;
}
