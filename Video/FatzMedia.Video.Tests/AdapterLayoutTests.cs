namespace FatzMedia.Video.Tests;

/// <summary>ENG-039: the fit and centring math the three adapters share.</summary>
public sealed class AdapterLayoutTests
{
    [Theory]
    [InlineData(800, 600, 0, 0, 800, 600)] // exactly 4:3
    [InlineData(1920, 1080, 240, 0, 1440, 1080)] // wide: pillarboxed
    [InlineData(400, 600, 0, 150, 400, 300)] // tall: letterboxed
    [InlineData(0, 0, 0, 0, 0, 0)]
    public void KaraokeLayout_FitsTheLargestCentred43Rectangle(
        double width, double height, double x, double y, double fitWidth, double fitHeight) =>
        Assert.Equal((x, y, fitWidth, fitHeight), KaraokeLayout.Fit(width, height));

    [Theory]
    [InlineData(0, 0, 1920, 1080, 800, 600, 560, 240)]
    [InlineData(1920, 0, 1280, 1024, 800, 600, 2160, 212)] // a screen right of the primary
    [InlineData(-1280, 0, 1280, 1024, 800, 600, -1040, 212)] // a screen left of the primary
    [InlineData(0, 0, 1280, 720, 1920, 1080, 0, 0)] // larger than the area: clamped
    public void PresentationPlacement_CentresTheWindowInTheArea(
        int areaX, int areaY, int areaWidth, int areaHeight, int width, int height, int expectedX, int expectedY) =>
        Assert.Equal((expectedX, expectedY), PresentationPlacement.Center(areaX, areaY, areaWidth, areaHeight, width, height));
}
