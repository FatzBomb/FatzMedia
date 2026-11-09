namespace FatzMedia.Video.Core.Cdg;

/// <summary>
/// Immutable-ish snapshot of the current CDG indexed-color render state.
/// This is the handoff object a future CDG presenter will consume instead of a CPU ARGB bitmap.
/// </summary>
public sealed class CdgRenderSnapshot(
    long revision,
    long paletteRevision,
    ReadOnlyMemory<byte> colorIndices,
    ReadOnlyMemory<int> paletteArgb,
    uint backgroundWashArgb,
    int backgroundColorIndex,
    IReadOnlyList<CdgDirtyRect> dirtyRects)
{
    public static CdgRenderSnapshot Empty { get; } = new(
        0,
        0,
        ReadOnlyMemory<byte>.Empty,
        ReadOnlyMemory<int>.Empty,
        0,
        0,
        []);

#pragma warning disable CA1822 // Public API instance properties; intentionally NOT static for callers.
    public int Width => CdgConstants.FrameWidth;
    public int Height => CdgConstants.FrameHeight;
#pragma warning restore CA1822
    public long Revision { get; } = revision;
    public long PaletteRevision { get; } = paletteRevision;
    public ReadOnlyMemory<byte> ColorIndices { get; } = colorIndices;
    public ReadOnlyMemory<int> PaletteArgb { get; } = paletteArgb;
    public uint BackgroundWashArgb { get; } = backgroundWashArgb;
    public int BackgroundColorIndex { get; } = backgroundColorIndex;
    public IReadOnlyList<CdgDirtyRect> DirtyRects { get; } = dirtyRects;
    public bool HasDirtyRegions => DirtyRects.Count > 0;

    /// <summary>
    /// The revision <see cref="DirtyRects"/> are relative to: they cover every change since the
    /// snapshot of that revision. A renderer holding another revision (it skipped a snapshot, or
    /// its texture is new) must upload the whole frame; -1 when unknown.
    /// </summary>
    public long DirtyBaseRevision { get; init; } = -1;

    /// <summary>
    /// Gets whether a renderer can draw the snapshot as it is: it carries no pixel data at all
    /// (nothing to draw), or a full frame of color indices and a palette. The software and
    /// Vulkan renderers refuse a frame whose snapshot is not well formed.
    /// </summary>
    public bool IsWellFormed => (ColorIndices.IsEmpty && PaletteArgb.IsEmpty)
        || (ColorIndices.Length >= Width * Height && !PaletteArgb.IsEmpty);
}
