using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Pipeline;

namespace FatzBombMedia.MediaControl.Tests;

public class MediaStateTests
{
    [Fact]
    public void Defaults_AreNoneAndNull()
    {
        var state = new MediaState();

        Assert.Equal(MediaContent.None, state.Content);
        Assert.Equal(MediaPlayerState.Null, state.PlaybackState);
        Assert.Null(state.SourcePath);
        Assert.Equal(0, state.PositionSeconds);
        Assert.Equal(0, state.DurationSeconds);
        Assert.False(state.IsOpen);
    }

    [Fact]
    public void SetOpened_SetsContentPathDurationAndResetsPosition()
    {
        var state = new MediaState();

        state.SetOpened(MediaContent.Audio, "song.mp3", 180.5);

        Assert.Equal(MediaContent.Audio, state.Content);
        Assert.Equal("song.mp3", state.SourcePath);
        Assert.Equal(180.5, state.DurationSeconds);
        Assert.Equal(0, state.PositionSeconds);
        Assert.Equal(MediaPlayerState.Stopped, state.PlaybackState);
        Assert.True(state.IsOpen);
    }

    [Fact]
    public void SetOpened_CleansNonFiniteDuration()
    {
        var state = new MediaState();

        state.SetOpened(MediaContent.Audio, "song.mp3", double.NaN);

        Assert.Equal(0, state.DurationSeconds);
    }

    [Fact]
    public void SetOpened_CleansNegativeDuration()
    {
        var state = new MediaState();

        state.SetOpened(MediaContent.Audio, "song.mp3", -10);

        Assert.Equal(0, state.DurationSeconds);
    }

    [Fact]
    public void SetPlaybackState_UpdatesState()
    {
        var state = new MediaState();

        state.SetPlaybackState(MediaPlayerState.Playing);

        Assert.Equal(MediaPlayerState.Playing, state.PlaybackState);
    }

    [Fact]
    public void SetPlaybackState_ShortCircuitsWhenUnchanged()
    {
        var state = new MediaState();
        state.SetPlaybackState(MediaPlayerState.Playing);

        // Setting same value again should be a no-op (no exception, same value)
        state.SetPlaybackState(MediaPlayerState.Playing);

        Assert.Equal(MediaPlayerState.Playing, state.PlaybackState);
    }

    [Fact]
    public void SetPosition_UpdatesPosition()
    {
        var state = new MediaState();

        state.SetPosition(42.5);

        Assert.Equal(42.5, state.PositionSeconds);
    }

    [Fact]
    public void SetPosition_CleansNonFiniteAndNegative()
    {
        var state = new MediaState();

        state.SetPosition(double.PositiveInfinity);

        Assert.Equal(0, state.PositionSeconds);
    }

    [Fact]
    public void SetPosition_ShortCircuitsWhenWithinOneMillisecond()
    {
        var state = new MediaState();
        state.SetPosition(10.0);

        // Difference < 0.001 → no-op
        state.SetPosition(10.0005);

        Assert.Equal(10.0, state.PositionSeconds);
    }

    [Fact]
    public void SetPosition_AcceptsChangeAboveOneMillisecondThreshold()
    {
        var state = new MediaState();
        state.SetPosition(10.0);

        // Difference clearly > 0.001 → updates (avoid floating-point boundary)
        state.SetPosition(10.005);

        Assert.Equal(10.005, state.PositionSeconds);
    }

    [Fact]
    public void SetDuration_UpdatesDuration()
    {
        var state = new MediaState();

        state.SetDuration(200.0);

        Assert.Equal(200.0, state.DurationSeconds);
    }

    [Fact]
    public void SetDuration_ClampsPositionToDurationWhenPositionExceeds()
    {
        var state = new MediaState();
        state.SetPosition(150.0);

        state.SetDuration(100.0);

        Assert.Equal(100.0, state.DurationSeconds);
        Assert.Equal(100.0, state.PositionSeconds);
    }

    [Fact]
    public void SetDuration_DoesNotClampWhenDurationIsZero()
    {
        var state = new MediaState();
        state.SetPosition(50.0);

        state.SetDuration(0);

        Assert.Equal(0, state.DurationSeconds);
        Assert.Equal(50.0, state.PositionSeconds);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var state = new MediaState();
        state.SetOpened(MediaContent.Audio | MediaContent.Cdg, "song.mp3", 180.0);
        state.SetPlaybackState(MediaPlayerState.Playing);
        state.SetPosition(90.0);

        state.Reset();

        Assert.Equal(MediaContent.None, state.Content);
        Assert.Equal(MediaPlayerState.Null, state.PlaybackState);
        Assert.Null(state.SourcePath);
        Assert.Equal(0, state.PositionSeconds);
        Assert.Equal(0, state.DurationSeconds);
        Assert.False(state.IsOpen);
    }

    [Fact]
    public void IsOpen_TrueWhenContentIsNotNone()
    {
        var state = new MediaState();

        Assert.False(state.IsOpen);

        state.SetOpened(MediaContent.Image, "photo.png", 0);

        Assert.True(state.IsOpen);
    }
}