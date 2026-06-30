namespace FatzBombMedia.MediaControl.Core.DxCdg;

/// <summary>
/// Immutable-ish snapshot of the current CDG indexed-color render state.
/// This is the handoff object a future DXCDG presenter will consume instead of a CPU ARGB bitmap.
/// </summary>
public sealed class DxCdgRenderSnapshot(
    long revision,
    long paletteRevision,
    ReadOnlyMemory<byte> colorIndices,
    ReadOnlyMemory<int> paletteArgb,
    uint backgroundWashArgb,
    int backgroundColorIndex,
    IReadOnlyList<DxCdgDirtyRect> dirtyRects)
{
    public static DxCdgRenderSnapshot Empty { get; } = new(
        0,
        0,
        ReadOnlyMemory<byte>.Empty,
        ReadOnlyMemory<int>.Empty,
        0,
        0,
        []);

    public int Width => DxCdgConstants.FrameWidth;
    public int Height => DxCdgConstants.FrameHeight;
    public long Revision { get; } = revision;
    public long PaletteRevision { get; } = paletteRevision;
    public ReadOnlyMemory<byte> ColorIndices { get; } = colorIndices;
    public ReadOnlyMemory<int> PaletteArgb { get; } = paletteArgb;
    public uint BackgroundWashArgb { get; } = backgroundWashArgb;
    public int BackgroundColorIndex { get; } = backgroundColorIndex;
    public IReadOnlyList<DxCdgDirtyRect> DirtyRects { get; } = dirtyRects;
    public bool HasDirtyRegions => DirtyRects.Count > 0;
}
