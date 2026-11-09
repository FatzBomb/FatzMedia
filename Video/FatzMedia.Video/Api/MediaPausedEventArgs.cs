#pragma warning disable IDE0130
namespace FatzMedia.Video;

public sealed class MediaPausedEventArgs(TimeSpan position) : EventArgs
{
    public TimeSpan Position { get; } = position;
}
