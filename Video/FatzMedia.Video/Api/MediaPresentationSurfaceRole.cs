#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Describes the role a presentation surface plays in the media-control output set.
/// </summary>
public enum MediaPresentationSurfaceRole
{
    /// <summary>The primary surface owned by the framework control.</summary>
    Primary = 0,

    /// <summary>A dedicated projector/audience output surface.</summary>
    Projector = 3
}
