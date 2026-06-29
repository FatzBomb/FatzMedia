namespace FatzBombMedia.MediaControl;

/// <summary>
/// Describes the role a presentation surface plays in the media-control output set.
/// </summary>
public enum MediaPresentationSurfaceRole
{
    /// <summary>The primary surface owned by the framework control.</summary>
    Primary = 0,

    /// <summary>An additional output surface, such as a projector/preview window.</summary>
    Secondary = 1,

    /// <summary>A preview output surface.</summary>
    Preview = 2,

    /// <summary>A dedicated projector/audience output surface.</summary>
    Projector = 3
}
