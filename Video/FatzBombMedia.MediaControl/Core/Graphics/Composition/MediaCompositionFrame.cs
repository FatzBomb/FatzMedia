using FatzBombMedia.MediaControl.Core.DxCdg;

namespace FatzBombMedia.MediaControl.Core.Graphics.Composition;

/// <summary>
/// Generic visual-frame description consumed by the single media compositor.
/// All media modes build one of these frames: video/image/audio background, optional wash,
/// optional overlay graphics, then one final present.
/// </summary>
public readonly record struct MediaCompositionFrame
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

    public IntPtr BackgroundD3D11TexturePointer { get; init; }
    public int BackgroundD3D11ArraySlice { get; init; }
    public int BackgroundD3D11Width { get; init; }
    public int BackgroundD3D11Height { get; init; }
    public double BackgroundD3D11FramesPerSecond { get; init; }
    public bool UseRetainedBackground { get; init; }

    public uint WashArgb { get; init; }
    public DxCdgRenderSnapshot? CdgOverlay { get; init; }

    public bool RetainedRefresh { get; init; }
    public string Reason { get; init; }

    public bool HasBackground => !BackgroundBgra.IsEmpty
        && BackgroundWidth > 0
        && BackgroundHeight > 0
        && BackgroundStride >= BackgroundWidth * 4;

    public bool HasD3D11Background => BackgroundD3D11TexturePointer != IntPtr.Zero
        && BackgroundD3D11Width > 0
        && BackgroundD3D11Height > 0;

    public bool HasRetainedBackground => UseRetainedBackground
        && BackgroundWidth > 0
        && BackgroundHeight > 0;

    public bool HasAnyBackground => HasBackground || HasD3D11Background || HasRetainedBackground;

    public bool HasWash => ((WashArgb >> 24) & 0xFF) != 0;

    public bool HasCdgOverlay
    {
        get
        {
            DxCdgRenderSnapshot? overlay = CdgOverlay;
            return overlay != null
                && !overlay.ColorIndices.IsEmpty
                && !overlay.PaletteArgb.IsEmpty;
        }
    }

    public bool HasShaderVisibleLayer => HasAnyBackground || HasWash || HasCdgOverlay;

    public bool HasAnyVisibleLayer => HasAnyBackground || HasWash || HasCdgOverlay;

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
            BackgroundD3D11TexturePointer = IntPtr.Zero,
            BackgroundD3D11ArraySlice = 0,
            BackgroundD3D11Width = 0,
            BackgroundD3D11Height = 0,
            BackgroundD3D11FramesPerSecond = 0,
            UseRetainedBackground = false,
            WashArgb = 0,
            CdgOverlay = null,
            RetainedRefresh = false,
            Reason = reason
        };
}
