using FatzMedia.Video.Core.Graphics.Cadence;

namespace FatzMedia.Video.Portable.Tests;

/// <summary>Stage 017A slice 2: the host-readable cadence report.</summary>
public sealed class CadenceAdviceTests
{
    [Theory]
    [InlineData(23.976023976, 24000, 1001)]
    [InlineData(29.97, 30000, 1001)]
    [InlineData(59.94, 60000, 1001)]
    [InlineData(60.004, 60, 1)]
    [InlineData(24, 24, 1)]
    [InlineData(143.856, 143856, 1000)]
    public void ToFrameRate_SnapsToExactRates(double hertz, long numerator, long denominator) =>
        Assert.Equal(new FrameRate(numerator, denominator), CadenceAdvice.ToFrameRate(hertz));

    [Theory]
    [InlineData(0)]
    [InlineData(-24)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(5000)]
    public void ToFrameRate_RejectsImplausibleRates(double hertz) => Assert.Null(CadenceAdvice.ToFrameRate(hertz));

    [Theory]
    [InlineData(0, 60)]
    [InlineData(1000, 60)] // a container timebase, not a cadence
    [InlineData(24, 0)]
    public void Describe_UnknownOrImplausibleRates_ReturnNull(double fps, double hz) => Assert.Null(CadenceAdvice.Describe(fps, hz));

    [Fact]
    public void Describe_24On60_IsThreeTwoPulldownWithSmoothRates()
    {
        var text = CadenceAdvice.Describe(24, 60);

        Assert.Equal(
            "Video 24 fps on a 60 Hz display: frames are shown for 3 and 2 refreshes in turn (3:2 pulldown). " +
            "Motion can look uneven (judder) on slow pans. Smooth display rates for this video: 24, 48, 72, 120, 144, 240 Hz.",
            text);
    }

    [Fact]
    public void Describe_23976On60_IsStillThreeTwo() =>
        Assert.Contains("3:2 pulldown", CadenceAdvice.Describe(23.976, 60));

    [Fact]
    public void Describe_23976On5994_SuggestsTheNtscRates() =>
        Assert.EndsWith("Smooth display rates for this video: 23.976, 47.952, 119.88 Hz.", CadenceAdvice.Describe(23.976, 59.94));

    [Theory]
    [InlineData(25, 50, "each frame is shown for 2 refreshes, so motion is smooth.")]
    [InlineData(24, 120, "each frame is shown for 5 refreshes, so motion is smooth.")]
    [InlineData(60, 60, "each frame is shown for one refresh, so motion is smooth.")]
    public void Describe_WholeMultiples_AreSmooth(double fps, double hz, string expectedEnding) =>
        Assert.EndsWith(expectedEnding, CadenceAdvice.Describe(fps, hz));

    [Fact]
    public void Describe_2997On60_ReportsTheOccasionalLongHold() =>
        Assert.Contains("frames are shown for 2 refreshes, but about every 16.7 s one is shown for 3.", CadenceAdvice.Describe(29.97, 60));

    [Fact]
    public void Describe_25On60_IsAnUnevenMix()
    {
        var text = CadenceAdvice.Describe(25, 60);

        Assert.Contains("frames are shown for 2 or 3 refreshes.", text);
        Assert.Contains("Smooth display rates for this video: 25, 50, 75, 100 Hz.", text);
    }

    [Fact]
    public void Describe_60On50_ReportsSkippedFrames()
    {
        var text = CadenceAdvice.Describe(60, 50);

        Assert.Contains("about 17% of frames are never shown", text);
        Assert.Contains("Smooth display rates for this video: 60, 120, 240 Hz.", text);
    }

    [Fact]
    public void Describe_NearMatchFasterVideo_ReportsOccasionalSkips() =>
        Assert.Contains("about every 16.7 s one frame is never shown", CadenceAdvice.Describe(60, 59.94));

    [Fact]
    public void Describe_UsualHoldOfOne_UsesTheSingular() =>
        Assert.Contains("frames are shown for one refresh, but about every 16.7 s one is shown for 2.", CadenceAdvice.Describe(59.94, 60));

    [Fact]
    public void Describe_SlowDriftBeyondAMinute_IsNotCalledSmooth() =>
        Assert.Contains("about every 66.7 s one is shown for 2", CadenceAdvice.Describe(60, 60.015));
}
