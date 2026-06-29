namespace FatzBombMedia.MediaControl;

/// <summary>
/// Describes how primary video content is displayed inside the media viewport.
/// Background media used behind overlays ignores this setting and always uses
/// <see cref="ZoomToFill" /> semantics.
/// </summary>
public enum VideoDisplayMode
{
    /// <summary>
    /// Show the video at its original/native pixel size, centered in the viewport.
    /// </summary>
    Original,

    /// <summary>
    /// Scale the video uniformly so the whole frame is visible. This is the default
    /// and matches the previous aspect-preserving video behavior.
    /// </summary>
    Zoom,

    /// <summary>
    /// Scale the video non-uniformly so it exactly fills the viewport. This may distort
    /// the video aspect ratio.
    /// </summary>
    Stretch,

    /// <summary>
    /// Scale the video uniformly until it fills the viewport, cropping excess edges
    /// when the viewport aspect differs from the video aspect.
    /// </summary>
    ZoomToFill
}
