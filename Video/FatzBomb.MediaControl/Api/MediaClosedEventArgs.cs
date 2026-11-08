#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

public sealed class MediaClosedEventArgs(MediaSourceInfo source) : EventArgs
{
    public MediaSourceInfo Source { get; } = source ?? throw new ArgumentNullException(nameof(source));
}
