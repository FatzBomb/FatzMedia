using FatzBomb.MediaControl.Core.Graphics.D3D.Presentation;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// Stage 017A slice 4: the refresh grid built from WaitForVBlank wake-ups keeps a stable phase and
/// period through wake-up jitter, skips late wake-ups, and starts over when the timing moves.
/// </summary>
public sealed class D3D11VblankGridTests
{
    private const long Frequency = 10_000_000;
    private const double Period = Frequency / 60.0;
    private const long Start = 50_000_000;

    [Fact]
    public void WakeUpJitter_LeavesAStableGrid()
    {
        var grid = new VblankGrid(Frequency);
        grid.SeedRefreshRate(60);
        var random = new Random(7);
        double worst = 0;
        for (var blank = 0; blank < 600; blank++)
        {
            var actual = Start + (blank * Period);
            grid.Observe((long)(actual + (random.NextDouble() * 10_000))); // wakes up 0..1 ms late
            if (blank > 60)
                worst = Math.Max(worst, Math.Abs(grid.LastVsyncTimestamp - (actual + 5_000)));
        }

        Assert.True(grid.IsValid);
        Assert.True(worst < 5_000, $"grid phase off by {worst / 10_000.0:F2} ms");
        Assert.InRange(grid.PeriodTicks, Period - 50, Period + 50);
    }

    [Fact]
    public void MissedBlanks_AdvanceTheGridByWholePeriods()
    {
        var grid = new VblankGrid(Frequency);
        grid.SeedRefreshRate(60);
        for (var blank = 0; blank < 10; blank++)
            grid.Observe((long)(Start + (blank * Period)));

        grid.Observe((long)(Start + (13 * Period))); // three blanks missed

        Assert.True(grid.IsValid);
        Assert.InRange(grid.LastVsyncTimestamp, Start + (13 * Period) - 200, Start + (13 * Period) + 200);
    }

    [Fact]
    public void ALateWakeUp_IsSkipped()
    {
        var grid = new VblankGrid(Frequency);
        grid.SeedRefreshRate(60);
        for (var blank = 0; blank < 10; blank++)
            grid.Observe((long)(Start + (blank * Period)));

        var before = grid.LastVsyncTimestamp;
        grid.Observe((long)(Start + (10 * Period) + (Period * 0.45))); // 7.5 ms late

        Assert.Equal(before, grid.LastVsyncTimestamp);
        Assert.True(grid.IsValid);
    }

    [Fact]
    public void ARefreshRateChange_IsMeasuredAgain()
    {
        var grid = new VblankGrid(Frequency);
        grid.SeedRefreshRate(60);
        for (var blank = 0; blank < 20; blank++)
            grid.Observe((long)(Start + (blank * Period)));

        // The monitor switches to 144 Hz.
        var newPeriod = Frequency / 144.0;
        var switchAt = Start + (20 * Period);
        for (var blank = 0; blank < 40; blank++)
            grid.Observe((long)(switchAt + (blank * newPeriod)));

        Assert.True(grid.IsValid);
        Assert.InRange(grid.PeriodTicks, newPeriod - 100, newPeriod + 100);
        Assert.InRange(grid.LastVsyncTimestamp, switchAt + (39 * newPeriod) - 200, switchAt + (39 * newPeriod) + 200);
        Assert.Equal(1, grid.Restarts);
        Assert.Contains("off the refresh period", grid.LastRestartReason, StringComparison.Ordinal);
    }

    [Fact]
    public void AfterARateChange_TheNominalPeriodIsUsed_WhenItMatches()
    {
        var grid = new VblankGrid(Frequency);
        grid.SeedRefreshRate(60);
        for (var blank = 0; blank < 20; blank++)
            grid.Observe((long)(Start + (blank * Period)));

        // Windows switches to 144 Hz; the cache reports it, and the first interval is noisy.
        var newPeriod = Frequency / 144.0;
        grid.SeedRefreshRate(144);
        var switchAt = Start + (20 * Period);
        var random = new Random(3);
        for (var blank = 0; blank < 12; blank++)
            grid.Observe((long)(switchAt + (blank * newPeriod) + (random.NextDouble() * 5_000)));

        Assert.True(grid.IsValid);
        Assert.InRange(grid.PeriodTicks, newPeriod - 300, newPeriod + 300); // one noisy interval: up to 5000 off
    }

    [Fact]
    public void ALowerRefreshRate_IsMeasuredAgain()
    {
        var grid = new VblankGrid(Frequency);
        grid.SeedRefreshRate(144);
        var oldPeriod = Frequency / 144.0;
        for (var blank = 0; blank < 40; blank++)
            grid.Observe((long)(Start + (blank * oldPeriod)));

        var switchAt = Start + (40 * oldPeriod);
        for (var blank = 0; blank < 30; blank++)
            grid.Observe((long)(switchAt + (blank * Period)));

        Assert.True(grid.IsValid);
        Assert.InRange(grid.PeriodTicks, Period - 100, Period + 100);
    }
}
