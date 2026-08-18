#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

public sealed class MediaPositionChangedEventArgs(TimeSpan position, TimeSpan duration) : EventArgs
{
    public TimeSpan Position { get; } = position;
    public TimeSpan Duration { get; } = duration;
}
