using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Tests;

namespace FatzMedia.Video.Portable.Tests;

/// <summary>
/// Stage 035A: during a seek-bar drag, playback carries on, and the release seek at the last
/// drag position is ignored so it cannot jump back to where the drag stopped.
/// </summary>
public sealed class SeekScrubTests
{
    [Fact]
    public void ReleaseSeekAtTheLastDragPosition_IsIgnored()
    {
        var engine = CreatePlayingEngine();
        var controls = new MediaControls(engine);

        Assert.True(controls.BeginScrub());
        Assert.True(controls.Seek(30));
        Assert.Equal(MediaPlayerState.Playing, engine.State);

        // The mouse is held still while playback continues past the drag position.
        engine.Position = 32.5;

        Assert.True(controls.Seek(30));
        Assert.True(controls.EndScrub());

        Assert.Equal(32.5, engine.Position);
        Assert.Equal(MediaPlayerState.Playing, engine.State);
        Assert.False(controls.IsScrubbing);
    }

    [Fact]
    public void ReleaseSeekAtANewPosition_Seeks()
    {
        var engine = CreatePlayingEngine();
        var controls = new MediaControls(engine);

        Assert.True(controls.BeginScrub());
        Assert.True(controls.Seek(30));
        Assert.True(controls.Seek(45));
        Assert.True(controls.EndScrub());

        Assert.Equal(45, engine.Position);
    }

    [Fact]
    public void ScrubDoesNotPauseOrRaiseTransportEvents()
    {
        var engine = CreatePlayingEngine();
        var controls = new MediaControls(engine);
        var events = new List<string>();
        controls.MediaPaused += (_, _) => events.Add("paused");
        controls.MediaStarted += (_, _) => events.Add("started");
        controls.StateChanged += (_, _) => events.Add("state");

        Assert.True(controls.BeginScrub());
        Assert.True(controls.Seek(10));
        Assert.True(controls.EndScrub());

        Assert.Equal(MediaPlayerState.Playing, engine.State);
        Assert.Empty(events);
    }

    [Fact]
    public void RepeatedSeekOutsideAScrub_StillSeeks()
    {
        var engine = CreatePlayingEngine();
        var controls = new MediaControls(engine);

        Assert.True(controls.Seek(30));
        engine.Position = 33;
        Assert.True(controls.Seek(30));

        Assert.Equal(30, engine.Position);
    }

    [Fact]
    public void FirstSeekOfAScrub_AlwaysSeeks()
    {
        var engine = CreatePlayingEngine();
        var controls = new MediaControls(engine);
        Assert.True(controls.Seek(30));
        engine.Position = 33;

        Assert.True(controls.BeginScrub());
        Assert.True(controls.Seek(30));

        Assert.Equal(30, engine.Position);
    }

    [Fact]
    public void CancelScrub_EndsTheScrub()
    {
        var engine = CreatePlayingEngine();
        var controls = new MediaControls(engine);
        Assert.True(controls.BeginScrub());
        Assert.True(controls.Seek(30));

        controls.CancelScrub();
        engine.Position = 33;

        Assert.False(controls.IsScrubbing);
        Assert.False(controls.EndScrub());
        Assert.True(controls.Seek(30));
        Assert.Equal(30, engine.Position);
    }

    [Fact]
    public void ScrubWithNothingOpen_IsRejected()
    {
        var controls = new MediaControls(new TestMediaEngine());

        Assert.False(controls.BeginScrub());
        Assert.False(controls.IsScrubbing);
        Assert.False(controls.EndScrub());
    }

    static TestMediaEngine CreatePlayingEngine()
    {
        var engine = new TestMediaEngine();
        _ = engine.Open(MediaInputSource.FromFile("song.mp3"));
        _ = engine.Play();
        engine.Position = 12;
        return engine;
    }
}
