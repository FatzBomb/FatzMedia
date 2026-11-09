#pragma warning disable IDE0130
namespace FatzMedia.Video;

public sealed class MediaLoopedEventArgs(TimeSpan position) : EventArgs
{
    public TimeSpan Position { get; } = position;
}
