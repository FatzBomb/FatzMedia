using FatzBomb.MediaControl.Core.Graphics.Cadence;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// Pins the Stage 017A cadence specification for representative source/display pairs.
/// </summary>
public sealed class DisplayCadenceTests
{
    private static readonly FrameRate Hz50 = FrameRate.FromInteger(50);
    private static readonly FrameRate Hz60 = FrameRate.FromInteger(60);
    private static readonly FrameRate Hz120 = FrameRate.FromInteger(120);

    [Fact]
    public void Fps24_On60Hz_Is32Pulldown()
    {
        CadenceReport report = DisplayCadence.Analyze(FrameRate.FromInteger(24), Hz60, 600);

        Assert.Equal(0, report.DroppedFrames);
        Assert.Equal((2, 3), (report.MinimumHold, report.MaximumHold));
        Assert.All(report.HoldPattern.Chunk(2), pair => Assert.Equal(5, pair.Sum()));
    }

    [Theory]
    [InlineData(25, 50, 2)]
    [InlineData(30, 60, 2)]
    [InlineData(60, 120, 2)]
    [InlineData(24, 120, 5)]
    [InlineData(60, 60, 1)]
    public void IntegerMultiples_AreJudderFree(int fps, int hz, int hold)
    {
        CadenceReport report = DisplayCadence.Analyze(FrameRate.FromInteger(fps), FrameRate.FromInteger(hz), 1200);

        Assert.True(report.IsJudderFree);
        Assert.Equal(hold, report.MaximumHold);
    }

    [Fact]
    public void Fps23_976_On60Hz_Is32PulldownWithRareExtraRepeat()
    {
        // 23.976 is 0.1 % slower than 24: over ~42 s one extra refresh repeat appears.
        CadenceReport report = DisplayCadence.Analyze(FrameRate.Fps23_976, Hz60, 60 * 60);

        Assert.Equal(0, report.DroppedFrames);
        Assert.Equal((2, 3), (report.MinimumHold, report.MaximumHold));
        var brokenPairs = report.HoldPattern.Chunk(2).Count(pair => pair.Length == 2 && pair.Sum() != 5);
        Assert.InRange(brokenPairs, 1, 5);
    }

    [Fact]
    public void Fps23_976_On59_94Hz_IsExact32Pulldown()
    {
        CadenceReport report = DisplayCadence.Analyze(FrameRate.Fps23_976, FrameRate.Hz59_94, 6000);

        Assert.Equal(0, report.DroppedFrames);
        Assert.All(report.HoldPattern.Chunk(2).Where(static pair => pair.Length == 2), pair => Assert.Equal(5, pair.Sum()));
    }

    [Fact]
    public void Fps30_On59_94Hz_NeverDropsButSometimesHoldsOneRefresh()
    {
        // The display is still faster than the source, so the latest-frame rule never skips a
        // frame; the 0.1 % excess shortens one hold from 2 refreshes to 1 every 1000 refreshes
        // (~16.7 s), each absorbing half a source frame.
        CadenceReport report = DisplayCadence.Analyze(FrameRate.FromInteger(30), FrameRate.Hz59_94, 60_000);

        Assert.Equal(0, report.DroppedFrames);
        Assert.Equal((1, 2), (report.MinimumHold, report.MaximumHold));
        Assert.InRange(report.HoldPattern.Count(static hold => hold == 1), 58, 60);
    }

    [Fact]
    public void Fps29_97_On60Hz_RepeatsAnExtraRefreshRarely()
    {
        CadenceReport report = DisplayCadence.Analyze(FrameRate.Fps29_97, Hz60, 6000);

        // 29.97 is 0.1 % slower than 30: one extra refresh every 500 frames (~16.7 s).
        Assert.Equal(0, report.DroppedFrames);
        Assert.Equal((2, 3), (report.MinimumHold, report.MaximumHold));
        Assert.Equal(5, report.HoldPattern.Count(static hold => hold == 3));
    }

    [Fact]
    public void Fps60_On50Hz_DropsOneFrameInSix()
    {
        CadenceReport report = DisplayCadence.Analyze(Hz60, Hz50, 500);

        Assert.Equal(100, report.DroppedFrames, tolerance: 1);
        Assert.Equal(1, report.MaximumHold);
    }

    [Fact]
    public void Fps25_On60Hz_Alternates2And3()
    {
        CadenceReport report = DisplayCadence.Analyze(FrameRate.FromInteger(25), Hz60, 600);

        Assert.Equal(0, report.DroppedFrames);
        Assert.Equal((2, 3), (report.MinimumHold, report.MaximumHold));
        Assert.Equal(12.0 / 5.0, report.HoldPattern.Average(static h => (double)h), precision: 2);
    }

    [Fact]
    public void Phase_ShiftsWhichRefreshShowsTheFirstNewFrame()
    {
        long[] noPhase = [.. DisplayCadence.Simulate(FrameRate.FromInteger(24), Hz60, 5).Select(static s => s.SourceFrame)];
        long[] halfFrame = [.. DisplayCadence.Simulate(FrameRate.FromInteger(24), Hz60, 5, phaseNumerator: 1, phaseDenominator: 48).Select(static s => s.SourceFrame)];

        Assert.Equal([0L, 0, 0, 1, 1], noPhase);
        Assert.Equal([0L, 0, 1, 1, 2], halfFrame);
    }

    [Fact]
    public void LongRuns_StayExactWithoutFloatingDrift()
    {
        // After exactly one hour of 59.94 Hz refreshes, 23.976 fps content is at frame 86313.
        var vsyncs = 60L * 60 * 60000 / 1001;
        Assert.Equal(86_313, DisplayCadence.FrameAt(FrameRate.Fps23_976, FrameRate.Hz59_94, vsyncs));
    }

    [Fact]
    public void MultiTarget_EachRefreshRateGetsItsOwnCadence()
    {
        CadenceReport primary = DisplayCadence.Analyze(FrameRate.FromInteger(24), Hz60, 600);
        CadenceReport projector = DisplayCadence.Analyze(FrameRate.FromInteger(24), Hz120, 1200);

        // Both targets show the same media time span (10 s) but with different holds.
        Assert.Equal(primary.DistinctFramesShown, projector.DistinctFramesShown);
        Assert.False(primary.IsJudderFree);
        Assert.True(projector.IsJudderFree);
    }

    [Fact]
    public void InvalidRates_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayCadence.Analyze(new FrameRate(0, 1), Hz60, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayCadence.Analyze(Hz60, new FrameRate(60, 0), 1));
    }
}
