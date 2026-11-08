using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Pipeline;

namespace FatzBomb.MediaControl.Tests;

public sealed class AudioPipelineTrimTests
{
    [Fact]
    public void CachedTrimWindowDefinesEffectiveDurationAndInitialPosition()
    {
        var engine = CreateEngineWithTrimWindow();
        using var pipeline = new AudioPipeline(engine);

        Assert.True(pipeline.Open("song.mp3"));

        Assert.Equal(90, pipeline.DurationSeconds);
        Assert.Equal(90, pipeline.State.DurationSeconds);
        Assert.Equal(0, pipeline.PositionSeconds);
        Assert.Equal(0, pipeline.State.PositionSeconds);
    }

    [Theory]
    [InlineData(-10, 5, 0)]
    [InlineData(40, 45, 40)]
    [InlineData(500, 95, 90)]
    public void SeekMapsEffectivePositionToSourceWindow(
        double requested,
        double expectedSource,
        double expectedEffective)
    {
        var engine = CreateEngineWithTrimWindow();
        using var pipeline = new AudioPipeline(engine);
        Assert.True(pipeline.Open("song.mp3"));

        Assert.True(pipeline.Seek(requested));

        Assert.Equal(expectedSource, engine.Position);
        Assert.Equal(expectedEffective, pipeline.PositionSeconds);
        Assert.Equal(expectedEffective, pipeline.State.PositionSeconds);
    }

    [Fact]
    public void PlayStartsAtPlayableSourceBoundary()
    {
        var engine = CreateEngineWithTrimWindow();
        using var pipeline = new AudioPipeline(engine);
        Assert.True(pipeline.Open("song.mp3"));
        engine.Position = 0;

        Assert.True(pipeline.Play());

        Assert.Equal(5, engine.Position);
        Assert.Equal(0, pipeline.PositionSeconds);
        Assert.Equal(MediaPlayerState.Playing, pipeline.State.PlaybackState);
    }

    [Fact]
    public void SourcePositionEventsArePublishedInEffectiveSpace()
    {
        var engine = CreateEngineWithTrimWindow();
        using var pipeline = new AudioPipeline(engine);
        Assert.True(pipeline.Open("song.mp3"));

        engine.RaisePositionChanged(45);

        Assert.Equal(40, pipeline.PositionSeconds);
        Assert.Equal(40, pipeline.State.PositionSeconds);
    }

    [Fact]
    public void TrimmedPlaybackEndPublishesEffectiveEnd()
    {
        var engine = CreateEngineWithTrimWindow();
        using var pipeline = new AudioPipeline(engine);
        Assert.True(pipeline.Open("song.mp3"));
        Assert.True(pipeline.Play());

        engine.RaisePlaybackEnded();

        Assert.Equal(90, pipeline.State.PositionSeconds);
        Assert.Equal(MediaPlayerState.Stopped, pipeline.State.PlaybackState);
    }

    [Fact]
    public void TrimmedLoopRestartsAtEffectiveZero()
    {
        var engine = CreateEngineWithTrimWindow();
        engine.LoopPlayback = true;
        using var pipeline = new AudioPipeline(engine);
        Assert.True(pipeline.Open("song.mp3"));
        Assert.True(pipeline.Play());

        engine.RaisePlaybackEnded();

        Assert.Equal(5, engine.Position);
        Assert.Equal(0, pipeline.State.PositionSeconds);
        Assert.Equal(MediaPlayerState.Playing, pipeline.State.PlaybackState);
    }

    [Fact]
    public void FullySilentWindowCannotStartPlayback()
    {
        var engine = new TestAudioEngine
        {
            Duration = 100,
            ActiveTrimWindow = new MediaAnalysisResult(
                100,
                100,
                100,
                0,
                [new SilenceRegion(0, 100)],
                1)
        };
        using var pipeline = new AudioPipeline(engine);
        Assert.True(pipeline.Open("silent.mp3"));

        Assert.False(pipeline.Play());
        Assert.Equal(MediaPlayerState.Stopped, pipeline.State.PlaybackState);
    }

    [Fact]
    public void StopAndReplayRestartAtPlayableBoundary()
    {
        var engine = CreateEngineWithTrimWindow();
        using var pipeline = new AudioPipeline(engine);
        Assert.True(pipeline.Open("song.mp3"));
        Assert.True(pipeline.Seek(40));
        Assert.True(pipeline.Play());

        Assert.True(pipeline.Stop());
        Assert.Equal(0, pipeline.PositionSeconds);
        Assert.True(pipeline.Play());
        Assert.Equal(5, engine.Position);
        Assert.Equal(0, pipeline.PositionSeconds);
    }

    [Fact]
    public void PauseAndResumePreserveEffectivePosition()
    {
        var engine = CreateEngineWithTrimWindow();
        using var pipeline = new AudioPipeline(engine);
        Assert.True(pipeline.Open("song.mp3"));
        Assert.True(pipeline.Seek(40));
        Assert.True(pipeline.Play());

        Assert.True(pipeline.Pause());
        Assert.True(pipeline.Play());

        Assert.Equal(45, engine.Position);
        Assert.Equal(40, pipeline.PositionSeconds);
    }

    [Fact]
    public void SeekToEffectiveEndThenPlayRestartsAtPlayableStart()
    {
        var engine = CreateEngineWithTrimWindow();
        using var pipeline = new AudioPipeline(engine);
        Assert.True(pipeline.Open("song.mp3"));
        Assert.True(pipeline.Seek(pipeline.DurationSeconds));

        Assert.True(pipeline.Play());

        Assert.Equal(5, engine.Position);
        Assert.Equal(0, pipeline.PositionSeconds);
        Assert.Equal(MediaPlayerState.Playing, pipeline.State.PlaybackState);
    }

    [Fact]
    public void SynchronizedOpenIgnoresAvailableTrimWindow()
    {
        var engine = CreateEngineWithTrimWindow();
        using var pipeline = new AudioPipeline(engine);

        Assert.True(pipeline.Open("song.mp3", ownState: false, analyzeSilence: false));

        Assert.Equal(100, pipeline.DurationSeconds);
        Assert.Equal(45, SetAndReadPosition(pipeline, engine, 45));
    }

    [Fact]
    public void MissingTrimWindowPreservesUnknownDurationSeekBehavior()
    {
        var engine = new TestAudioEngine { Duration = 0 };
        using var pipeline = new AudioPipeline(engine);
        Assert.True(pipeline.Open("song.mp3"));

        Assert.True(pipeline.Seek(12));

        Assert.Equal(12, engine.Position);
        Assert.Equal(12, pipeline.PositionSeconds);
    }

    private static TestAudioEngine CreateEngineWithTrimWindow() => new()
    {
        Duration = 100,
        ActiveTrimWindow = new MediaAnalysisResult(
            100,
            5,
            95,
            90,
            [new SilenceRegion(0, 5), new SilenceRegion(95, 100)],
            1)
    };

    private static double SetAndReadPosition(
        AudioPipeline pipeline,
        TestAudioEngine engine,
        double sourcePosition)
    {
        engine.Position = sourcePosition;
        return pipeline.PositionSeconds;
    }
}
