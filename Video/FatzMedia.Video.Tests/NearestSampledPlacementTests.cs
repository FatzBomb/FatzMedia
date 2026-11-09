using FatzMedia.Video.Core.Cdg;
using FatzMedia.Video.Core.Graphics.Composition;
using FatzMedia.Video.Core.Graphics.Geometry;

namespace FatzMedia.Video.Tests;

/// <summary>
/// ENG-014: the nearest-sampled placement and CDG snapshot check the software and Vulkan
/// renderers share from Core.
/// </summary>
public sealed class NearestSampledPlacementTests
{
    [Fact]
    public void FractionalEdges_RoundAwayFromZero()
    {
        // 300x216 into 640x480: 640x461 at top 9.5, so rows 10..470 are drawn (bottom edge 471).
        var placement = NearestSampledPlacement.Create(300, 216, 640, 480, MediaCompositionStretchMode.UniformFit);

        Assert.Equal((0, 10, 640, 471), (placement.ClippedLeft, placement.ClippedTop, placement.ClippedRight, placement.ClippedBottom));
        Assert.False(placement.IsEmpty);
        Assert.Equal(0, placement.GetSourceY(10));
        Assert.Equal(215, placement.GetSourceY(470));
        Assert.Equal(299, placement.GetSourceX(639));
    }

    [Fact]
    public void ADestinationPastTheOutput_IsClippedAndSampledFromItsOwnEdges()
    {
        // UniformToFill: 8x4 into 4x4 is 8x4 wide at left -2.
        var placement = NearestSampledPlacement.Create(8, 4, 4, 4, MediaCompositionStretchMode.UniformToFill);

        Assert.Equal(-2, placement.Destination.Left);
        Assert.Equal((0, 0, 4, 4), (placement.ClippedLeft, placement.ClippedTop, placement.ClippedRight, placement.ClippedBottom));
        Span<int> columns = stackalloc int[4];
        placement.MapColumns(columns);
        Assert.Equal([2, 3, 4, 5], columns.ToArray());
    }

    [Fact]
    public void HalfPixelEdges_RoundAwayFromZero()
    {
        Assert.Equal(3, NearestSampledPlacement.RoundEdge(2.5));
        Assert.Equal(-3, NearestSampledPlacement.RoundEdge(-2.5));
    }

    [Fact]
    public void ADestinationOutsideTheOutput_IsEmpty() =>
        Assert.True(NearestSampledPlacement.Create(10, 10, 100, 100, new RectD(120, 0, 10, 10)).IsEmpty);

    [Fact]
    public void CanvasPlacement_MapsTheRegionThroughTheCanvas()
    {
        // A 50x20 region at (10, 30) of a 100x100 canvas shown on a 200x200 output.
        var placement = NearestSampledPlacement.CreateCanvasPlacement(50, 20, 10, 30, 100, 100, 200, 200);

        Assert.Equal((20, 60, 120, 100), (placement.ClippedLeft, placement.ClippedTop, placement.ClippedRight, placement.ClippedBottom));
    }

    [Fact]
    public void CdgSnapshots_AreWellFormedOnlyWhenEmptyOrComplete()
    {
        var indices = new byte[CdgConstants.TotalPixels];
        var palette = new int[16];

        Assert.True(CdgRenderSnapshot.Empty.IsWellFormed);
        Assert.True(new CdgRenderSnapshot(1, 1, indices, palette, 0, 0, []).IsWellFormed);
        Assert.False(new CdgRenderSnapshot(1, 1, indices.AsMemory(1), palette, 0, 0, []).IsWellFormed);
        Assert.False(new CdgRenderSnapshot(1, 1, indices, ReadOnlyMemory<int>.Empty, 0, 0, []).IsWellFormed);
        Assert.False(new CdgRenderSnapshot(1, 1, ReadOnlyMemory<byte>.Empty, palette, 0, 0, []).IsWellFormed);
    }
}
