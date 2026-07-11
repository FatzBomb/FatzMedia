using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Pipeline;

namespace FatzBombMedia.MediaControl.Tests;

public class PlaybackClockTests
{
    [Fact]
    public void ClockSource_SetTempo_ExposesTheRequestedTempo()
    {
        IClockSource clock = new PlaybackClock();

        clock.SetTempo(125);

        Assert.Equal(125, clock.TempoPercent);
    }

    [Fact]
    public void ClockSource_Seek_ClampsToTheConfiguredDuration()
    {
        IClockSource clock = new PlaybackClock { DurationSeconds = 10 };

        clock.Seek(25);

        Assert.Equal(10, clock.PositionSeconds);
    }

    [Fact]
    public void ClockSource_Stop_ResetsPositionAndRunningState()
    {
        IClockSource clock = new PlaybackClock { DurationSeconds = 10 };
        clock.Start(5);

        clock.Stop();

        Assert.False(clock.IsRunning);
        Assert.Equal(0, clock.PositionSeconds);
    }
}
