using FatzBomb.MediaControl.Core.Video;

namespace FatzBomb.MediaControl.Tests;

public class DwmCompositionTimingTests
{
    [Theory]
    [InlineData(60u, 1u, 1.0 / 60.0)]
    [InlineData(24000u, 1001u, 1001.0 / 24000.0)]
    [InlineData(600u, 1u, 1.0 / 600.0)]
    public void GetRefreshIntervalSeconds_ValidRefreshRate_ReturnsInterval(uint numerator, uint denominator, double expected) =>
        Assert.Equal(expected, DwmCompositionTiming.GetRefreshIntervalSeconds(numerator, denominator), precision: 12);

    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(60u, 0u)]
    [InlineData(22u, 1u)]
    [InlineData(601u, 1u)]
    public void GetRefreshIntervalSeconds_InvalidRefreshRate_Returns60HzFallback(uint numerator, uint denominator) =>
        Assert.Equal(1.0 / 60.0, DwmCompositionTiming.GetRefreshIntervalSeconds(numerator, denominator), precision: 12);
}
