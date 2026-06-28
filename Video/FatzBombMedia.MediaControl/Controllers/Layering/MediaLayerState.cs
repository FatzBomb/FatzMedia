using FatzBombMedia.MediaControl.Core.Abstractions;

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

    /// <summary>
    /// Converts the decoded media content flags into renderer layer roles. Keep this
    /// as the single source of truth for future renderer work.
    ///
    /// Layer map used by the current renderer:
    /// - Normal video file: Primary=Video. Uses the primary video path only and obeys
    ///   VideoDisplayMode: Original, Zoom, Stretch, or ZoomToFill. No wash/overlay layers.
    /// - Normal image file: Primary=Image. Uses the still-image primary/static path and
    ///   obeys VideoDisplayMode. No wash/overlay layers.
    /// - Audio-only file: Primary=Audio. No visual layer unless the host/manual background
    ///   feature is enabled separately.
    /// - CDG-only file: Primary=CdgOnly, Overlay=Cdg. Layer 2 is WashLayer full viewport;
    ///   Layer 3 is DXCDG layer Uniform-fitted to the viewport. No background-media layer
    ///   unless a companion/manual image or video is assigned.
    /// - Audio+CDG file: Primary=AudioCdg, Overlay=Cdg. Same CDG wash/overlay rules as
    ///   CDG-only; audio remains the primary timed media.
    /// - CDG/audio+CDG with companion background image/video: Background=Image/Video and
    ///   Overlay=Cdg. Layer 1 is BackgroundMediaLayer and always ZoomToFill/full viewport;
    ///   Layer 2 is WashLayer full viewport; Layer 3 is DXCDG layer Uniform-fitted. The
    ///   primary VideoDisplayMode must not affect these overlay-session layers.
    /// </summary>
    public static MediaLayerState From(MediaContent content, MediaBackgroundKind background)
    {
        var hasAudio = content.HasFlag(MediaContent.Audio);
        var hasVideo = content.HasFlag(MediaContent.Video);
        var hasImage = content.HasFlag(MediaContent.Image);
        var hasCdg = content.HasFlag(MediaContent.Cdg);

        PrimaryMediaKind primary = hasVideo
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

        ForegroundOverlayKind overlay = hasCdg ? ForegroundOverlayKind.Cdg : ForegroundOverlayKind.None;
        return new MediaLayerState(primary, background, overlay);
    }

    public override string ToString()
        => $"Primary={Primary}, Background={Background}, Overlay={ForegroundOverlay}, UsesBackgroundPresentation={UsesBackgroundPresentation}";
}
