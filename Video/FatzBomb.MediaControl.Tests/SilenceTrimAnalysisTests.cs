using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Core.Configuration;

namespace FatzBomb.MediaControl.Tests;

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

    [Fact]
    public void CacheRejectsVersionMismatchAndInvalidResults()
    {
        var cache = new MediaAnalysisCache(capacity: 2, analyzerVersion: 2);
        var currentKey = new MediaAnalysisCacheKey("media-a", 2, default);
        var staleKey = currentKey with { AnalyzerVersion = 1 };
        var validResult = SilenceTrimCalculator.Calculate(10, [], 1, default);
        var invalidResult = new MediaAnalysisResult(10, 8, 2, 10, [], 1);

        Assert.False(cache.TryStore(staleKey, validResult));
        Assert.False(cache.TryStore(currentKey, invalidResult));
        Assert.True(cache.TryStore(currentKey, validResult));
        Assert.False(cache.TryGet(staleKey, out _));
        Assert.True(cache.TryGet(currentKey, out var cached));
        Assert.Same(validResult, cached);
    }

    [Fact]
    public void CacheUsesLeastRecentlyUsedBoundedEviction()
    {
        var cache = new MediaAnalysisCache(capacity: 2);
        var first = new MediaAnalysisCacheKey("first", MediaAnalysisVersions.Current, default);
        var second = new MediaAnalysisCacheKey("second", MediaAnalysisVersions.Current, default);
        var third = new MediaAnalysisCacheKey("third", MediaAnalysisVersions.Current, default);
        var result = SilenceTrimCalculator.Calculate(10, [], 1, default);

        Assert.True(cache.TryStore(first, result));
        Assert.True(cache.TryStore(second, result));
        Assert.True(cache.TryGet(first, out _));
        Assert.True(cache.TryStore(third, result));

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet(first, out _));
        Assert.False(cache.TryGet(second, out _));
        Assert.True(cache.TryGet(third, out _));
    }

    [Fact]
    public void LocalFileKeyIncludesMetadataVersionAndOptions()
    {
        var filePath = Path.GetTempFileName();
        try
        {
            var options = new SilenceTrimOptions { Enabled = true };
            Assert.True(MediaAnalysisCacheKey.TryCreateForFile(
                filePath,
                MediaAnalysisVersions.Current,
                options,
                out var first));

            File.AppendAllText(filePath, "changed");

            Assert.True(MediaAnalysisCacheKey.TryCreateForFile(
                filePath,
                MediaAnalysisVersions.Current,
                options,
                out var changed));
            Assert.NotEqual(first, changed);
            Assert.NotEqual(first, first with { AnalyzerVersion = 2 });
            Assert.NotEqual(first, first with { TrimOptions = default });
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void LocalFileKeyFailsClosedForMissingFile()
    {
        Assert.False(MediaAnalysisCacheKey.TryCreateForFile(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.missing"),
            MediaAnalysisVersions.Current,
            default,
            out _));
    }
}
