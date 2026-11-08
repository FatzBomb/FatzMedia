#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

public sealed class MediaPausedEventArgs(TimeSpan position) : EventArgs
{
    public TimeSpan Position { get; } = position;
}
