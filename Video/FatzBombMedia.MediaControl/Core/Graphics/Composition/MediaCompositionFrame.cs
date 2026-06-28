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
    public bool BackgroundUniformToFill { get; init; }
    public long BackgroundRevision { get; init; }

    public uint WashArgb { get; init; }
    public DxCdgRenderSnapshot? CdgOverlay { get; init; }

    public bool RetainedRefresh { get; init; }
    public string Reason { get; init; }

    public bool HasBackground => !BackgroundBgra.IsEmpty
        && BackgroundWidth > 0
        && BackgroundHeight > 0
        && BackgroundStride >= BackgroundWidth * 4;

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

    public bool HasAnyVisibleLayer => HasBackground || HasWash || HasCdgOverlay;

    public static MediaCompositionFrame Empty(MediaCompositionClearMode clearMode = MediaCompositionClearMode.Black, uint clearArgb = 0xFF000000, string reason = "empty")
        => new()
        {
            ClearMode = clearMode,
            ClearArgb = clearArgb,
            BackgroundBgra = ReadOnlyMemory<byte>.Empty,
            BackgroundWidth = 0,
            BackgroundHeight = 0,
            BackgroundStride = 0,
            BackgroundUniformToFill = false,
            BackgroundRevision = -1,
            WashArgb = 0,
            CdgOverlay = null,
            RetainedRefresh = false,
            Reason = reason
        };
}
