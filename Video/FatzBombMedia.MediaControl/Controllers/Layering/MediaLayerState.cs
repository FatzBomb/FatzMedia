using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Controllers;

namespace FatzBombMedia.MediaControl.Controllers.Layering;

internal enum MediaLayerRole
{
    None,
    Primary,
    Background,
    ForegroundOverlay
}

internal enum PrimaryMediaKind
{
    None,
    Audio,
    Video,
    Image,
    CdgOnly,
    AudioCdg
}

internal enum ForegroundOverlayKind
{
    None,
    Cdg
}

/// <summary>
/// Stable role classification for the currently loaded media.  This is session state, not
/// frame state: background-layer behavior must not depend on whether CDG has just invalidated
/// or whether a retained CDG texture has already been uploaded.
/// </summary>
internal readonly record struct MediaLayerState(
    PrimaryMediaKind Primary,
    MediaBackgroundKind Background,
    ForegroundOverlayKind ForegroundOverlay)
{
    public static MediaLayerState Empty { get; } = new(
        PrimaryMediaKind.None,
        MediaBackgroundKind.None,
        ForegroundOverlayKind.None);

    public bool HasOverlaySession => ForegroundOverlay != ForegroundOverlayKind.None;
    public bool HasBackgroundMedia => Background != MediaBackgroundKind.None;

    /// <summary>
    /// True when the renderer should use background-media rules: full viewport destination,
    /// UniformToFill scaling, silent/looping video, and no dependency on overlay update cadence.
    /// </summary>
    public bool UsesBackgroundPresentation => HasOverlaySession && HasBackgroundMedia;

    public static MediaLayerState From(MediaContent content, MediaBackgroundKind background)
    {
        var hasAudio = content.HasFlag(MediaContent.Audio);
        var hasVideo = content.HasFlag(MediaContent.Video);
        var hasImage = content.HasFlag(MediaContent.Image);
        var hasCdg = content.HasFlag(MediaContent.Cdg);

        var primary = hasVideo
            ? PrimaryMediaKind.Video
            : hasImage
                ? PrimaryMediaKind.Image
                : hasCdg && hasAudio
                    ? PrimaryMediaKind.AudioCdg
                    : hasCdg
                        ? PrimaryMediaKind.CdgOnly
                        : hasAudio
                            ? PrimaryMediaKind.Audio
                            : PrimaryMediaKind.None;

        var overlay = hasCdg ? ForegroundOverlayKind.Cdg : ForegroundOverlayKind.None;
        return new MediaLayerState(primary, background, overlay);
    }

    public override string ToString()
        => $"Primary={Primary}, Background={Background}, Overlay={ForegroundOverlay}, UsesBackgroundPresentation={UsesBackgroundPresentation}";
}
