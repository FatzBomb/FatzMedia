using FatzMedia.Video.Core.Audio;
using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Tests;

public sealed class SilenceTrimAnalysisTests
{
    [Fact]
    public void DefaultsDisableTrimmingWithPinnedAnalysisValues()
    {
        SilenceTrimOptions options = default;

        Assert.False(options.Enabled);
        Assert.Equal(-50, options.ThresholdDecibels);
        Assert.Equal(TimeSpan.FromSeconds(0.5), options.MinimumSilenceDuration);
        Assert.Equal(TimeSpan.FromSeconds(0.1), options.Padding);
        Assert.True(options.TrimLeading);
        Assert.True(options.TrimTrailing);
        Assert.Equal(options, new MediaPlayerConfiguration().SilenceTrim);
    }

    [Fact]
    public void InvalidOptionValuesUseDefaultsOrClampToSupportedRanges()
    {
        var options = new SilenceTrimOptions
        {
            ThresholdDecibels = double.NaN,
            MinimumSilenceDuration = TimeSpan.FromSeconds(-1),
            Padding = TimeSpan.FromMinutes(1)
        };

        Assert.Equal(-50, options.ThresholdDecibels);
        Assert.Equal(TimeSpan.Zero, options.MinimumSilenceDuration);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Padding);

        options = options with { ThresholdDecibels = 4 };
        Assert.Equal(0, options.ThresholdDecibels);
    }

    [Fact]
    public void DisabledOptionsPreserveSourceAndDetectedRegions()
    {
        var result = SilenceTrimCalculator.Calculate(
            100,
            [new SilenceRegion(0, 2), new SilenceRegion(95, 100)],
            1.25,
            default);

        Assert.Equal(100, result.SourceDuration);
        Assert.Equal(0, result.PlayableStart);
        Assert.Equal(100, result.PlayableEnd);
        Assert.Equal(100, result.PlayableDuration);
        Assert.Equal(100, result.EffectiveDuration);
        Assert.Equal(2, result.SilenceRegions.Count);
        Assert.Equal(1.25, result.NormalizationGain);
        Assert.True(MediaAnalysisResultValidator.IsValid(result));
    }

    [Fact]
    public void EnabledOptionsTrimBothEdgesAndRetainPadding()
    {
        var options = new SilenceTrimOptions { Enabled = true };

        var result = SilenceTrimCalculator.Calculate(
            100,
            [new SilenceRegion(0, 2), new SilenceRegion(95, 100)],
            1,
            options);

        Assert.Equal(1.9, result.PlayableStart, 10);
        Assert.Equal(95.1, result.PlayableEnd, 10);
        Assert.Equal(93.2, result.PlayableDuration, 10);
        Assert.Equal(result.PlayableDuration, result.EffectiveDuration);
    }

    [Fact]
    public void MinimumAndIndependentEdgeControlsAreHonored()
    {
        var options = new SilenceTrimOptions
        {
            Enabled = true,
            TrimLeading = false,
            MinimumSilenceDuration = TimeSpan.FromSeconds(1)
        };

        var result = SilenceTrimCalculator.Calculate(
            10,
            [new SilenceRegion(0, 2), new SilenceRegion(9.5, 10)],
            1,
            options);

        Assert.Equal(0, result.PlayableStart);
        Assert.Equal(10, result.PlayableEnd);

        result = SilenceTrimCalculator.Calculate(
            10,
            [new SilenceRegion(0, 2), new SilenceRegion(8, 10)],
            1,
            options);

        Assert.Equal(0, result.PlayableStart);
        Assert.Equal(8.1, result.PlayableEnd, 10);
    }

    [Fact]
    public void TrailingTrimCanBeDisabledIndependently()
    {
        var options = new SilenceTrimOptions
        {
            Enabled = true,
            TrimTrailing = false
        };

        var result = SilenceTrimCalculator.Calculate(
            10,
            [new SilenceRegion(0, 2), new SilenceRegion(8, 10)],
            1,
            options);

        Assert.Equal(1.9, result.PlayableStart, 10);
        Assert.Equal(10, result.PlayableEnd);
    }

    [Fact]
    public void FullySpecifiedDefaultsHaveCanonicalOptionEquality()
    {
        var explicitDefaults = new SilenceTrimOptions
        {
            Enabled = false,
            ThresholdDecibels = SilenceTrimOptions.DefaultThresholdDecibels,
            MinimumSilenceDuration = TimeSpan.FromSeconds(SilenceTrimOptions.DefaultMinimumSilenceSeconds),
            Padding = TimeSpan.FromSeconds(SilenceTrimOptions.DefaultPaddingSeconds),
            TrimLeading = true,
            TrimTrailing = true
        };

        Assert.Equal(default, explicitDefaults);
    }

    [Fact]
    public void MalformedRegionsAreIgnoredAndOverlappingEdgesCannotInvertWindow()
    {
        var options = new SilenceTrimOptions
        {
            Enabled = true,
            MinimumSilenceDuration = TimeSpan.Zero,
            Padding = TimeSpan.Zero
        };

        var result = SilenceTrimCalculator.Calculate(
            10,
            [
                new SilenceRegion(double.NaN, 1),
                new SilenceRegion(5, 4),
                new SilenceRegion(-2, 8),
                new SilenceRegion(3, 12)
            ],
            1,
            options);

        Assert.Equal(8, result.PlayableStart);
        Assert.Equal(8, result.PlayableEnd);
        Assert.Equal(0, result.PlayableDuration);
        Assert.True(MediaAnalysisResultValidator.IsValid(result));
    }

    [Fact]
    public void FullySilentSourceProducesAnEmptyPlayableWindow()
    {
        var result = SilenceTrimCalculator.Calculate(
            10,
            [new SilenceRegion(0, 10)],
            1,
            new SilenceTrimOptions
            {
                Enabled = true,
                Padding = TimeSpan.Zero
            });

        Assert.Equal(10, result.PlayableStart);
        Assert.Equal(10, result.PlayableEnd);
        Assert.Equal(0, result.PlayableDuration);
        Assert.Equal(0, result.EffectiveDuration);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void CalculatorRejectsInvalidNormalizationGain(double normalizationGain)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SilenceTrimCalculator.Calculate(10, [], normalizationGain, default));
    }

    [Fact]
    public void ValidatorRejectsInconsistentDurationsAndRegions()
    {
        var invalidDuration = new MediaAnalysisResult(10, 2, 8, 9, [], 1);
        var invalidRegion = new MediaAnalysisResult(
            10,
            0,
            10,
            10,
            [new SilenceRegion(9, 11)],
            1);

        Assert.False(MediaAnalysisResultValidator.IsValid(invalidDuration));
        Assert.False(MediaAnalysisResultValidator.IsValid(invalidRegion));
    }
}
