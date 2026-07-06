using FatzBombMedia.MediaControl.Pipeline;

namespace FatzBombMedia.MediaControl.Tests;

public class AudioPresentationClockTests
{
    [Fact]
    public void GetPosition_WhenNotRunning_ReturnsCleanedClampedAudioPosition()
    {
        var clock = new AudioPresentationClock();

        var position = clock.GetPosition(42.5, 100.0);

        Assert.Equal(42.5, position);
    }

    [Fact]
    public void GetPosition_WhenNotRunning_ClampsToDuration()
    {
        var clock = new AudioPresentationClock();

        var position = clock.GetPosition(150.0, 100.0);

        Assert.Equal(100.0, position);
    }

    [Fact]
    public void GetPosition_WhenNotRunning_CleansNonFiniteAndNegative()
    {
        var clock = new AudioPresentationClock();

        Assert.Equal(0, clock.GetPosition(double.NaN, 100.0));
        Assert.Equal(0, clock.GetPosition(double.PositiveInfinity, 100.0));
        Assert.Equal(0, clock.GetPosition(-5.0, 100.0));
    }

    [Fact]
    public void GetPosition_WhenNotRunningAndNoDuration_ReturnsCleanedPosition()
    {
        var clock = new AudioPresentationClock();

        var position = clock.GetPosition(42.0, 0.0);

        Assert.Equal(42.0, position);
    }

    [Fact]
    public void Start_MakesClockRunning()
    {
        var clock = new AudioPresentationClock();

        clock.Start(10.0);

        // Immediately after start, estimate ≈ anchor (no time elapsed)
        var position = clock.GetPosition(10.0, 100.0);

        Assert.Equal(10.0, position, 0.5);
    }

    [Fact]
    public void Pause_StopsClockAndReturnsAudioPosition()
    {
        var clock = new AudioPresentationClock();
        clock.Start(10.0);

        clock.Pause(20.0);

        // Not running → returns cleaned audio position directly
        var position = clock.GetPosition(20.0, 100.0);
        Assert.Equal(20.0, position);
    }

    [Fact]
    public void Reset_StopsClock()
    {
        var clock = new AudioPresentationClock();
        clock.Start(10.0);

        clock.Reset(15.0);

        // Not running → returns cleaned audio position
        var position = clock.GetPosition(15.0, 100.0);
        Assert.Equal(15.0, position);
    }

    [Fact]
    public void GetPosition_HardCorrectionReanchorsToAudioPosition()
    {
        var clock = new AudioPresentationClock();
        clock.Start(0.0);

        // Audio jumped far ahead (drift >> HardCorrectionThreshold=0.09).
        // Estimate ≈ 0 (no time elapsed), drift ≈ 5.0 → hard correction → reanchor to 5.0.
        var position = clock.GetPosition(5.0, 100.0);

        Assert.Equal(5.0, position, 0.01);
    }

    [Fact]
    public void GetPosition_RunningClampsToDuration()
    {
        var clock = new AudioPresentationClock();
        clock.Start(0.0);

        // Audio position beyond duration → hard correction reanchors, then clamped
        var position = clock.GetPosition(150.0, 100.0);

        Assert.Equal(100.0, position, 0.01);
    }

    [Fact]
    public void GetPosition_IsMonotonicWithinOneMillisecondTolerance()
    {
        var clock = new AudioPresentationClock();
        clock.Start(0.0);

        // First call returns ~0
        var first = clock.GetPosition(0.0, 100.0);

        // Second call with a lower audio position should not go backward beyond 0.001
        var second = clock.GetPosition(0.0, 100.0);

        Assert.True(second >= first - 0.001);
    }

    [Fact]
    public void GetPosition_RunningCleansNonFiniteAudioPosition()
    {
        var clock = new AudioPresentationClock();
        clock.Start(0.0);

        // Non-finite audio position → Cleaned to 0 → drift = 0 - estimate ≈ 0 → no hard correction
        var position = clock.GetPosition(double.NaN, 100.0);

        Assert.True(position >= 0);
        Assert.True(position <= 1.0); // tiny elapsed time, no drift
    }
}