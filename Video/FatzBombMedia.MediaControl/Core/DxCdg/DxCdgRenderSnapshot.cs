namespace FatzBombMedia.MediaControl.Core.DxCdg;

/// <summary>
/// Immutable-ish snapshot of the current CDG indexed-color render state.
/// This is the handoff object a future DXCDG presenter will consume instead of a CPU ARGB bitmap.
/// </summary>
public sealed class DxCdgRenderSnapshot
{
    public static DxCdgRenderSnapshot Empty { get; } = new(
        0,
        ReadOnlyMemory<byte>.Empty,
        ReadOnlyMemory<int>.Empty,
        0,
        0,
        Array.Empty<DxCdgDirtyRect>());

    public DxCdgRenderSnapshot(
        long revision,
        ReadOnlyMemory<byte> colorIndices,
        ReadOnlyMemory<int> paletteArgb,
        uint backgroundWashArgb,
        int backgroundColorIndex,
        IReadOnlyList<DxCdgDirtyRect> dirtyRects)
    {
        Revision = revision;
        ColorIndices = colorIndices;
        PaletteArgb = paletteArgb;
        BackgroundWashArgb = backgroundWashArgb;
        BackgroundColorIndex = backgroundColorIndex;
        DirtyRects = dirtyRects;
    }

    public int Width => DxCdgConstants.FrameWidth;
    public int Height => DxCdgConstants.FrameHeight;
    public long Revision { get; }
    public ReadOnlyMemory<byte> ColorIndices { get; }
    public ReadOnlyMemory<int> PaletteArgb { get; }
    public uint BackgroundWashArgb { get; }
    public int BackgroundColorIndex { get; }
    public IReadOnlyList<DxCdgDirtyRect> DirtyRects { get; }
    public bool HasDirtyRegions => DirtyRects.Count > 0;
}
