using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Tests;

namespace FatzBomb.MediaControl.Portable.Tests;

/// <summary>
/// Stage 035A: a seek-bar drag holds playback until it ends, so releasing the bar never replays
/// the part that played while the mouse was still down.
/// </summary>
public sealed class SeekScrubTests
{
    [Fact]
    public void ScrubWhilePlaying_HoldsPlaybackSilentlyAndResumesOnceAtTheTarget()
    {
        TestMediaEngine engine = CreatePlayingEngine();
        var controls = new MediaControls(engine);
        var events = RecordEvents(controls);

        Assert.True(controls.BeginScrub());
        Assert.True(controls.IsScrubbing);
        Assert.Equal(MediaPlayerState.Paused, engine.State);

        Assert.True(controls.Seek(30));
        Assert.True(controls.Seek(45));
        Assert.Equal(MediaPlayerState.Paused, engine.State);

        var playsBefore = engine.PlayCount;
        Assert.True(controls.EndScrub());

        Assert.False(controls.IsScrubbing);
        Assert.Equal(MediaPlayerState.Playing, engine.State);
        Assert.Equal(playsBefore + 1, engine.PlayCount);
        Assert.Equal(45, engine.Position);
        Assert.Empty(events);
    }

    [Fact]
    public void ScrubWhilePaused_StaysPaused()
    {
        TestMediaEngine engine = CreatePlayingEngine();
        _ = engine.Pause();
        var controls = new MediaControls(engine);

        Assert.True(controls.BeginScrub());
        Assert.True(controls.Seek(10));
        var playsBefore = engine.PlayCount;
        Assert.True(controls.EndScrub());

        Assert.Equal(MediaPlayerState.Paused, engine.State);
        Assert.Equal(playsBefore, engine.PlayCount);
    }

    [Fact]
    public void HostPauseDuringScrub_CancelsTheResumeAndIsReported()
    {
        TestMediaEngine engine = CreatePlayingEngine();
        var controls = new MediaControls(engine);
        Assert.True(controls.BeginScrub());
        var events = RecordEvents(controls);

        Assert.True(controls.Pause());
        Assert.Contains("paused", events);
        Assert.Contains("state:Playing->Paused", events);

        var playsBefore = engine.PlayCount;
        Assert.True(controls.EndScrub());
        Assert.Equal(MediaPlayerState.Paused, engine.State);
        Assert.Equal(playsBefore, engine.PlayCount);
    }

    [Fact]
    public void HostPlayDuringScrub_PlaysNowAndEndScrubDoesNotPlayAgain()
    {
        TestMediaEngine engine = CreatePlayingEngine();
        var controls = new MediaControls(engine);
        Assert.True(controls.BeginScrub());

        Assert.True(controls.Play());
        var playsBefore = engine.PlayCount;
        Assert.True(controls.EndScrub());

        Assert.Equal(MediaPlayerState.Playing, engine.State);
        Assert.Equal(playsBefore, engine.PlayCount);
    }

    [Fact]
    public void CancelScrub_EndsWithoutResuming()
    {
        TestMediaEngine engine = CreatePlayingEngine();
        var controls = new MediaControls(engine);
        Assert.True(controls.BeginScrub());

        controls.CancelScrub();

        Assert.False(controls.IsScrubbing);
        Assert.False(controls.EndScrub());
        Assert.Equal(MediaPlayerState.Paused, engine.State);
    }

    [Fact]
    public void BeginScrubTwice_KeepsTheFirstResumeDecision()
    {
        TestMediaEngine engine = CreatePlayingEngine();
        var controls = new MediaControls(engine);

        Assert.True(controls.BeginScrub());
        Assert.True(controls.BeginScrub());
        Assert.True(controls.EndScrub());

        Assert.Equal(MediaPlayerState.Playing, engine.State);
    }

    [Fact]
    public void ScrubWithNothingOpen_IsRejected()
    {
        var controls = new MediaControls(new TestMediaEngine());

        Assert.False(controls.BeginScrub());
        Assert.False(controls.IsScrubbing);
        Assert.False(controls.EndScrub());
    }

    private static TestMediaEngine CreatePlayingEngine()
    {
        var engine = new TestMediaEngine();
        _ = engine.Open(MediaInputSource.FromFile("song.mp3"));
        _ = engine.Play();
        engine.Position = 12;
        return engine;
    }

    private static List<string> RecordEvents(MediaControls controls)
    {
        var events = new List<string>();
        controls.MediaPaused += (_, _) => events.Add("paused");
        controls.MediaStarted += (_, _) => events.Add("started");
        controls.StateChanged += (_, e) => events.Add($"state:{e.PreviousState}->{e.CurrentState}");
        return events;
    }
}
