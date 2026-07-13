using FatzBombMedia.MediaControl.Core.Graphics.Geometry;

namespace FatzBombMedia.MediaControl.Tests;

public sealed class ViewportGeometryTests
{
    [Fact]
    public void MapCanvasRegionToViewport_PreservesCanvasAspectAndPlacement()
    {
        RectD result = ViewportGeometry.MapCanvasRegionToViewport(
            new RectD(480, 540, 960, 270),
            1920,
            1080,
            1000,
            1000);

        Assert.Equal(250, result.Left, 3);
        Assert.Equal(500, result.Top, 3);
        Assert.Equal(500, result.Width, 3);
        Assert.Equal(140.625, result.Height, 3);
    }
}
