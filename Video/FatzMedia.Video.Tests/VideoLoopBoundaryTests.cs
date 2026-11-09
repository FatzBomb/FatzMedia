using FatzMedia.Video.Pipeline;

namespace FatzMedia.Video.Tests;

/// <summary>Stage 041A: the loop-boundary rules extracted from VideoPipeline.</summary>
public sealed class VideoLoopBoundaryTests
{
    [Theory]
    [InlineData(24, 1 / 24.0)]
    [InlineData(30, 1 / 30.0)]
    [InlineData(60, 0.02)]    // 16.7 ms raised to 20 ms
    [InlineData(120, 0.02)]
    [InlineData(5, 0.12)]     // at most 120 ms
    [InlineData(0, 0.05)]     // unknown frame rate
    public void EndTolerance_IsOneFrameWithinBounds(double fps, double expected) =>
        Assert.Equal(expected, VideoLoopBoundary.EndToleranceSeconds(fps), 9);

    [Theory]
    [InlineData(10, 12, 10)]
    [InlineData(12, 10, 10)]
    [InlineData(10, 0, 10)]
    [InlineData(double.NaN, 12, 12)]
    [InlineData(0, 12, 12)]
    public void TheLoopBoundary_IsTheShorterKnownDuration(double video, double media, double expected) =>
        Assert.Equal(expected, VideoLoopBoundary.LoopBoundarySeconds(video, media));

    [Theory]
    [InlineData(10.25, 10, 0.25)]
    [InlineData(9.5, 10, 0)]
    [InlineData(25, 10, 5)]          // more than a lap past: wrapped
    [InlineData(double.NaN, 10, 0)]
    [InlineData(5, 0, 0)]
    public void Overflow_LandsInTheNextLap(double position, double boundary, double expected) =>
        Assert.Equal(expected, VideoLoopBoundary.LoopOverflowPosition(position, boundary), 9);

    [Fact]
    public void TheNaturalEnd_NeedsPlayingAndAKnownDuration()
    {
        Assert.True(VideoLoopBoundary.HasReachedNaturalEnd(true, 10, 9.97, 0.05));
        Assert.False(VideoLoopBoundary.HasReachedNaturalEnd(true, 10, 9.9, 0.05));
        Assert.False(VideoLoopBoundary.HasReachedNaturalEnd(false, 10, 10, 0.05));
        Assert.False(VideoLoopBoundary.HasReachedNaturalEnd(true, 0, 10, 0.05));
    }

    [Fact]
    public void ALoopRestarts_WhenDrainedAtTheBoundaryOrPastItWithNothingQueued()
    {
        Assert.True(VideoLoopBoundary.ShouldRestartLoop(true, 0, 9.97, 10, 0.05));
        Assert.False(VideoLoopBoundary.ShouldRestartLoop(true, 3, 9.97, 10, 0.05));
        Assert.False(VideoLoopBoundary.ShouldRestartLoop(false, 0, 10.02, 10, 0.05));
        Assert.True(VideoLoopBoundary.ShouldRestartLoop(false, 0, 10.06, 10, 0.05));
    }

    [Fact]
    public void ADrainedDecoder_EndsVideoOnlyAtOnceAndWithAudioAtTheBoundary()
    {
        Assert.True(VideoLoopBoundary.HasDrainedAtEnd(true, 0, hasAudio: false, position: 3, loopBoundary: 10, tolerance: 0.05));
        Assert.False(VideoLoopBoundary.HasDrainedAtEnd(true, 0, hasAudio: true, position: 3, loopBoundary: 10, tolerance: 0.05));
        Assert.True(VideoLoopBoundary.HasDrainedAtEnd(true, 0, hasAudio: true, position: 9.96, loopBoundary: 10, tolerance: 0.05));
        Assert.False(VideoLoopBoundary.HasDrainedAtEnd(false, 0, hasAudio: false, position: 10, loopBoundary: 10, tolerance: 0.05));
        Assert.False(VideoLoopBoundary.HasDrainedAtEnd(true, 1, hasAudio: false, position: 10, loopBoundary: 10, tolerance: 0.05));
    }
}
