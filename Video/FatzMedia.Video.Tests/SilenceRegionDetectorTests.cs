using FatzMedia.Video.Core.Audio;

namespace FatzMedia.Video.Tests;

public sealed class SilenceRegionDetectorTests
{
    [Theory]
    [InlineData(-50, 0.0031622776601683794)]
    [InlineData(0, 1)]
    [InlineData(-96, 0.00001584893192461114)]
    [InlineData(double.NaN, 0.0031622776601683794)]
    public void DecibelThresholdConvertsToLinearLevel(double decibels, double expected)
    {
        Assert.Equal(expected, SilenceRegionDetector.ConvertDecibelsToLinear(decibels), 12);
    }

    [Fact]
    public void ConsecutiveSilentSamplesMergeIntoOrderedRegions()
    {
        var regions = SilenceRegionDetector.Detect(
            [
                new SilenceLevelSample(0.3, 0.4, 0.001),
                new SilenceLevelSample(0, 0.1, 0),
                new SilenceLevelSample(0.1, 0.2, 0.002),
                new SilenceLevelSample(0.2, 0.3, 0.5),
                new SilenceLevelSample(0.4, 0.5, 0.001)
            ],
            -50);

        Assert.Equal(
            [new SilenceRegion(0, 0.2), new SilenceRegion(0.3, 0.5)],
            regions);
    }

    [Fact]
    public void PeakAtThresholdIsSilentAndPeakAboveThresholdIsNot()
    {
        var threshold = SilenceRegionDetector.ConvertDecibelsToLinear(-50);

        var regions = SilenceRegionDetector.Detect(
            [
                new SilenceLevelSample(0, 0.1, threshold),
                new SilenceLevelSample(0.1, 0.2, threshold + 0.000001),
                new SilenceLevelSample(0.2, 0.3, threshold)
            ],
            -50);

        Assert.Equal(
            [new SilenceRegion(0, 0.1), new SilenceRegion(0.2, 0.3)],
            regions);
    }

    [Fact]
    public void InvalidSamplesAreIgnored()
    {
        var regions = SilenceRegionDetector.Detect(
            [
                new SilenceLevelSample(double.NaN, 0.1, 0),
                new SilenceLevelSample(0.1, 0.1, 0),
                new SilenceLevelSample(0.1, 0.2, double.NaN),
                new SilenceLevelSample(0.2, 0.3, -1),
                new SilenceLevelSample(0.3, 0.4, 0)
            ],
            -50);

        Assert.Equal([new SilenceRegion(0.3, 0.4)], regions);
    }

    [Fact]
    public void NoSilentSamplesProducesNoRegions()
    {
        var regions = SilenceRegionDetector.Detect(
            [
                new SilenceLevelSample(0, 0.1, 0.5),
                new SilenceLevelSample(0.1, 0.2, 1)
            ],
            -50);

        Assert.Empty(regions);
    }

    [Fact]
    public void FullySilentSamplesProduceOneSourceSpanningRegion()
    {
        var regions = SilenceRegionDetector.Detect(
            [
                new SilenceLevelSample(0, 0.1, 0),
                new SilenceLevelSample(0.1, 0.2, 0),
                new SilenceLevelSample(0.2, 0.25, 0)
            ],
            -50);

        Assert.Equal([new SilenceRegion(0, 0.25)], regions);
    }

    [Fact]
    public void DetectorCalculatorAndCacheFormValidatedAnalysisPipeline()
    {
        var options = new SilenceTrimOptions
        {
            Enabled = true,
            Padding = TimeSpan.Zero
        };
        var regions = SilenceRegionDetector.Detect(
            [
                new SilenceLevelSample(0, 1, 0),
                new SilenceLevelSample(1, 9, 0.5),
                new SilenceLevelSample(9, 10, 0)
            ],
            options.ThresholdDecibels);
        var result = SilenceTrimCalculator.Calculate(10, regions, 1.25, options);
        var cache = new MediaAnalysisCache(capacity: 1);
        var key = new MediaAnalysisCacheKey("pipeline", MediaAnalysisVersions.Current, options);

        Assert.True(MediaAnalysisResultValidator.IsValid(result));
        Assert.True(cache.TryStore(key, result));
        Assert.True(cache.TryGet(key, out var cached));
        Assert.Same(result, cached);
        Assert.Equal(1, result.PlayableStart);
        Assert.Equal(9, result.PlayableEnd);
    }

    [Fact]
    public void DefaultScanBlockMatchesPinnedEdgePrecision()
    {
        Assert.Equal(0.1, AudioSilenceAnalyzer.BlockDurationSeconds);
    }
}
