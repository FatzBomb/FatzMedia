#pragma warning disable IDE0130
namespace FatzMedia.Video;

public sealed class MediaLoadedEventArgs(MediaSourceInfo source, TimeSpan duration) : EventArgs
{
    public MediaSourceInfo Source { get; } = source ?? throw new ArgumentNullException(nameof(source));
    public TimeSpan Duration { get; } = duration;
}
