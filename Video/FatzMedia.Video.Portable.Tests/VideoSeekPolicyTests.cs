using FatzMedia.Video.Pipeline;

namespace FatzMedia.Video.Tests;

public sealed class VideoSeekPolicyTests
{
    [Theory]
    [InlineData(double.NaN, 100, 0)]
    [InlineData(double.PositiveInfinity, 100, 0)]
    [InlineData(-3, 100, 0)]
    [InlineData(42, 100, 42)]
    [InlineData(150, 100, 100)]
    [InlineData(150, 0, 150)]
    public void NormalizeTarget_ClampsToKnownDuration(double seconds, double duration, double expected) =>
        Assert.Equal(expected, VideoSeekPolicy.NormalizeTarget(seconds, duration));

    [Theory]
    [InlineData(10.0, double.NaN, true)]
    [InlineData(10.0, 10.02, false)]
    [InlineData(10.0, 10.024, false)]
    [InlineData(10.0, 10.03, true)]
    public void IsMeaningfullyDifferentTarget_Uses25MillisecondThreshold(double target, double previous, bool expected) =>
        Assert.Equal(expected, VideoSeekPolicy.IsMeaningfullyDifferentTarget(target, previous));

    [Theory]
    [InlineData(9.9, 10, 0.5, false, true)]
    [InlineData(9.9, 10, 0.5, true, false)]
    [InlineData(9.9995, 10, 0.5, true, true)]
    [InlineData(10.2, 10, 0.5, true, true)]
    [InlineData(11, 10, 0.5, false, false)]
    public void IsFrameAcceptable_PreviewEitherSideCommitAtOrAfter(double frame, double target, double distance, bool commit, bool expected) =>
        Assert.Equal(expected, VideoSeekPolicy.IsFrameAcceptable(frame, target, distance, commit));

    [Theory]
    [InlineData(62.0, 60.4, 60.4)]
    [InlineData(62.0, 63.5, 63.5)]
    [InlineData(62.0, double.NaN, 62.0)]
    [InlineData(62.0, double.PositiveInfinity, 62.0)]
    [InlineData(62.0, -1.0, 62.0)]
    public void ScrubSettleTarget_IsTheShownPreviewFrameWhenThereIsOne(double target, double shownFrame, double expected) =>
        Assert.Equal(expected, VideoSeekPolicy.GetScrubSettleTarget(target, shownFrame));

    [Theory]
    [InlineData(true, true, 85, 32)]
    [InlineData(false, true, 125, 72)]
    [InlineData(true, false, 65, 20)]
    [InlineData(false, false, 95, 44)]
    public void Budgets_DependOnPreviewAndHardware(bool preview, bool hardware, int budget, int attempts)
    {
        Assert.Equal(budget, VideoSeekPolicy.GetDecodeBudgetMilliseconds(preview, hardware));
        Assert.Equal(attempts, VideoSeekPolicy.GetDecodeMaxAttempts(preview, hardware));
    }
}

public sealed class AdaptiveSeekPumpTimingTests
{
    [Fact]
    public void NoMeasurement_UsesDefaultInterval() =>
        Assert.Equal(75, new AdaptiveSeekPumpTiming().IntervalMilliseconds);

    [Fact]
    public void FirstMeasurement_IsTakenAsIs_ThenEma()
    {
        var timing = new AdaptiveSeekPumpTiming();

        timing.Record(TimeSpan.FromMilliseconds(100));
        Assert.Equal(100, timing.ProcessingMilliseconds);
        Assert.Equal(175, timing.IntervalMilliseconds); // 100 * 1.5 + 25

        timing.Record(TimeSpan.FromMilliseconds(20));
        Assert.Equal(80, timing.ProcessingMilliseconds, precision: 10); // 0.75 * 100 + 0.25 * 20
        Assert.Equal(145, timing.IntervalMilliseconds);
    }

    [Theory]
    [InlineData(1, 75)]
    [InlineData(1000, 300)]
    public void Interval_IsClamped(double measuredMs, int expected)
    {
        var timing = new AdaptiveSeekPumpTiming();
        timing.Record(TimeSpan.FromMilliseconds(measuredMs));

        Assert.Equal(expected, timing.IntervalMilliseconds);
    }

    [Fact]
    public void NonPositiveMeasurements_AreIgnored_AndResetRestoresDefault()
    {
        var timing = new AdaptiveSeekPumpTiming();
        timing.Record(TimeSpan.Zero);
        timing.Record(TimeSpan.FromMilliseconds(-5));
        Assert.Equal(0, timing.ProcessingMilliseconds);

        timing.Record(TimeSpan.FromMilliseconds(200));
        timing.Reset();

        Assert.Equal(75, timing.IntervalMilliseconds);
    }
}
