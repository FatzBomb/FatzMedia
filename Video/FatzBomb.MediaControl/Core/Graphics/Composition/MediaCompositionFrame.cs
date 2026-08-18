using FatzBomb.MediaControl.Core.Cdg;
using FatzBomb.MediaControl.Core.Graphics;

namespace FatzBomb.MediaControl.Core.Graphics.Composition;

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
    public string Reason { get; init; }

    public bool HasBackground => !BackgroundBgra.IsEmpty
        && BackgroundWidth > 0
        && BackgroundHeight > 0
        && BackgroundStride >= BackgroundWidth * 4;

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
            CdgRenderSnapshot? overlay = CdgOverlay;
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
