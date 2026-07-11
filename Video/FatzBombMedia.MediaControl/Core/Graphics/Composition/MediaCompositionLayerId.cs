namespace FatzBombMedia.MediaControl.Core.Graphics.Composition;

/// <summary>
/// Stable identity for retained composition slots.  The identity is what a future
/// compositor cache keys on; media type still only contributes optional layers.
/// </summary>
public enum MediaCompositionLayerId
{
    Background = 0,
    Wash = 1,
    Overlay = 2,
    Subtitle = 3
}
