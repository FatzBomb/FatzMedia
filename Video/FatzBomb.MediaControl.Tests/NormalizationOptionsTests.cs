using FatzBomb.MediaControl.Core.Configuration;

namespace FatzBomb.MediaControl.Tests;

/// <summary>037A: the loudness normalization options (FatzAudio engine).</summary>
public sealed class NormalizationOptionsTests
{
    [Fact]
    public void Default_IsTheStreamingStandard_WithThePerUserCache()
    {
        NormalizationOptions options = default;

        Assert.Equal(-14, options.TargetLufs);
        Assert.Equal(-1, options.TruePeakCeilingDbtp);
        Assert.Null(options.CacheDirectory);
        Assert.Equal(options, new MediaPlayerConfiguration().Normalization);
        Assert.Equal(options, new NormalizationOptions());
    }

    [Theory]
    [InlineData(-16, -16)]
    [InlineData(-80, -60)]
    [InlineData(3, 0)]
    [InlineData(double.NaN, -14)]
    public void Target_IsClampedToFatzAudiosRange(double value, double expected) =>
        Assert.Equal(expected, new NormalizationOptions { TargetLufs = value }.TargetLufs);

    [Theory]
    [InlineData(-2, -2)]
    [InlineData(-30, -20)]
    [InlineData(1, 0)]
    [InlineData(double.PositiveInfinity, -1)]
    public void Ceiling_IsClampedToFatzAudiosRange(double value, double expected) =>
        Assert.Equal(expected, new NormalizationOptions { TruePeakCeilingDbtp = value }.TruePeakCeilingDbtp);
}
