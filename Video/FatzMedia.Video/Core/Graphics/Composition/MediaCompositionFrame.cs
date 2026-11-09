using FatzMedia.Video.Core.Cdg;

namespace FatzMedia.Video.Core.Graphics.Composition;

/// <summary>
/// Generic visual-frame description consumed by the single media compositor.
/// All media modes build one of these frames: video/image/audio background, optional wash,
/// optional overlay graphics, then one final present.
/// </summary>
public readonly partial record struct MediaCompositionFrame
{
    public MediaCompositionClearMode ClearMode { get; init; }
    public uint ClearArgb { get; init; }

    public ReadOnlyMemory<byte> BackgroundBgra { get; init; }
    public int BackgroundWidth { get; init; }
    public int BackgroundHeight { get; init; }
    public int BackgroundStride { get; init; }
    public MediaCompositionStretchMode BackgroundStretchMode { get; init; }

    /// <summary>
    /// Transitional compatibility flag for older call sites. New composition code should use
    /// <see cref="BackgroundStretchMode" />. When <see cref="BackgroundStretchMode" /> is
    /// <see cref="MediaCompositionStretchMode.Auto" />, this flag maps to UniformToFill.
    /// </summary>
    public bool BackgroundUniformToFill { get; init; }
    public long BackgroundRevision { get; init; }

    public NativeVideoFrameResource? BackgroundNativeResource { get; init; }
    public int BackgroundNativeWidth { get; init; }
    public int BackgroundNativeHeight { get; init; }
    public double BackgroundNativeFramesPerSecond { get; init; }
    public bool UseRetainedBackground { get; init; }
    public BaseVisualTransform BaseVisualTransform { get; init; }

    public uint WashArgb { get; init; }
    public CdgRenderSnapshot? CdgOverlay { get; init; }
    public bool SuppressCdgOverlayDraw { get; init; }
    public ReadOnlyMemory<byte> SubtitleBgra { get; init; }
    public int SubtitleWidth { get; init; }
    public int SubtitleHeight { get; init; }
    public int SubtitleStride { get; init; }
    public int SubtitleCanvasLeft { get; init; }
    public int SubtitleCanvasTop { get; init; }
    public int SubtitleCanvasWidth { get; init; }
    public int SubtitleCanvasHeight { get; init; }
    public long SubtitleRevision { get; init; }

    public bool RetainedRefresh { get; init; }

    /// <summary>
    /// The targets this composition is for, or null for every target (Stage 017A slice 5:
    /// display-locked video presents each display's surfaces at that display's refreshes).
    /// </summary>
    public MediaCompositionTargetFilter? Targets { get; init; }

    /// <summary>Whether a target receives this composition (always, without a filter).</summary>
    public bool IsForTarget(bool isPrimary, string surfaceId) => Targets?.Includes(isPrimary, surfaceId) ?? true;
    public string Reason { get; init; }

    public bool HasBackground => !BackgroundBgra.IsEmpty
        && BackgroundWidth > 0
        && BackgroundHeight > 0
        && BackgroundStride >= BackgroundWidth * 4;

    /// <summary>
    /// Gets whether the CPU background pixels, when there are any, describe a whole image: a
    /// positive size, a stride of at least one row and a buffer of at least <c>stride * height</c>
    /// bytes (ENG-039). Renderers refuse a frame whose background is not well formed instead of
    /// reading past its buffer.
    /// </summary>
    public bool IsBackgroundWellFormed => BackgroundBgra.IsEmpty
        || (BackgroundWidth > 0
            && BackgroundHeight > 0
            && BackgroundStride >= (long)BackgroundWidth * 4
            && BackgroundBgra.Length >= (long)BackgroundStride * BackgroundHeight);

    public bool HasNativeBackground => BackgroundNativeResource is { IsValid: true }
        && BackgroundNativeWidth > 0
        && BackgroundNativeHeight > 0;

    public bool HasRetainedBackground => UseRetainedBackground
        && BackgroundWidth > 0
        && BackgroundHeight > 0;

    public bool HasAnyBackground => HasBackground || HasNativeBackground || HasRetainedBackground;

    public bool HasWash => ((WashArgb >> 24) & 0xFF) != 0;

    public bool HasCdgOverlay
    {
        get
        {
            var overlay = CdgOverlay;
            return overlay != null
                && !overlay.ColorIndices.IsEmpty
                && !overlay.PaletteArgb.IsEmpty;
        }
    }

    public bool HasSubtitleOverlay => !SubtitleBgra.IsEmpty
        && SubtitleWidth > 0
        && SubtitleHeight > 0
        && SubtitleStride >= SubtitleWidth * 4;

    public bool HasShaderVisibleLayer => HasAnyBackground || HasWash || HasCdgOverlay || HasSubtitleOverlay;

    /// <summary>
    /// Gets the straight-alpha 0xAARRGGBB colour every renderer clears the target to:
    /// transparent for <see cref="MediaCompositionClearMode.Transparent"/> and
    /// <see cref="MediaCompositionClearMode.PreservePrevious"/>, <see cref="ClearArgb"/> as given for
    /// <see cref="MediaCompositionClearMode.ConfiguredColor"/>, and otherwise <see cref="ClearArgb"/>
    /// with zero meaning opaque black.
    /// </summary>
    public uint ResolveClearArgb() => ClearMode switch
    {
        MediaCompositionClearMode.Transparent or MediaCompositionClearMode.PreservePrevious => 0u,
        MediaCompositionClearMode.ConfiguredColor => ClearArgb,
        _ => ClearArgb == 0 ? 0xFF000000u : ClearArgb
    };

    public MediaCompositionStretchMode EffectiveBackgroundStretchMode => BackgroundStretchMode switch
    {
        MediaCompositionStretchMode.Auto when BackgroundUniformToFill => MediaCompositionStretchMode.UniformToFill,
        MediaCompositionStretchMode.Auto => MediaCompositionStretchMode.UniformFit,
        _ => BackgroundStretchMode
    };

    public static MediaCompositionFrame Empty(MediaCompositionClearMode clearMode = MediaCompositionClearMode.Black, uint clearArgb = 0xFF000000, string reason = "empty")
        => new()
        {
            ClearMode = clearMode,
            ClearArgb = clearArgb,
            BackgroundBgra = ReadOnlyMemory<byte>.Empty,
            BackgroundWidth = 0,
            BackgroundHeight = 0,
            BackgroundStride = 0,
            BackgroundStretchMode = MediaCompositionStretchMode.UniformFit,
            BackgroundUniformToFill = false,
            BackgroundRevision = -1,
            BackgroundNativeResource = null,
            BackgroundNativeWidth = 0,
            BackgroundNativeHeight = 0,
            BackgroundNativeFramesPerSecond = 0,
            UseRetainedBackground = false,
            BaseVisualTransform = BaseVisualTransform.Identity,
            WashArgb = 0,
            CdgOverlay = null,
            SuppressCdgOverlayDraw = false,
            SubtitleBgra = ReadOnlyMemory<byte>.Empty,
            SubtitleWidth = 0,
            SubtitleHeight = 0,
            SubtitleStride = 0,
            SubtitleCanvasLeft = 0,
            SubtitleCanvasTop = 0,
            SubtitleCanvasWidth = 0,
            SubtitleCanvasHeight = 0,
            SubtitleRevision = -1,
            RetainedRefresh = false,
            Reason = reason
        };
}
