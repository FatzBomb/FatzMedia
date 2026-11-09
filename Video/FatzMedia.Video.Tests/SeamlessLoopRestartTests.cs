using FatzMedia.Video.Core.Video;

namespace FatzMedia.Video.Tests;

public class SeamlessLoopRestartTests
{
    [Fact]
    public void ShouldStopTopUp_StopsAtEndOfStream() =>
        Assert.True(SeamlessLoopRestart.ShouldStopTopUp(currentFrameCount: 0, framesBefore: 0, frameThreshold: 10, endOfStream: true));

    [Fact]
    public void ShouldStopTopUp_StopsWhenThresholdReached() =>
        Assert.True(SeamlessLoopRestart.ShouldStopTopUp(currentFrameCount: 10, framesBefore: 2, frameThreshold: 10, endOfStream: false));

    [Fact]
    public void ShouldStopTopUp_StopsWhenThresholdExceeded() =>
        Assert.True(SeamlessLoopRestart.ShouldStopTopUp(currentFrameCount: 11, framesBefore: 2, frameThreshold: 10, endOfStream: false));

    [Fact]
    public void ShouldStopTopUp_StopsWhenQueueGrew() =>
        Assert.True(SeamlessLoopRestart.ShouldStopTopUp(currentFrameCount: 3, framesBefore: 2, frameThreshold: 10, endOfStream: false));

    [Fact]
    public void ShouldStopTopUp_ContinuesWhenBelowThresholdAndQueueUnchanged() =>
        Assert.False(SeamlessLoopRestart.ShouldStopTopUp(currentFrameCount: 2, framesBefore: 2, frameThreshold: 10, endOfStream: false));

    [Fact]
    public void ShouldStopTopUp_ContinuesFromEmptyQueueWhileDecoderPipelines() =>
        // B-frame codecs can consume several packets before emitting the first frame:
        // queue still 0, not yet at threshold, not EOF -> keep going.
        Assert.False(SeamlessLoopRestart.ShouldStopTopUp(currentFrameCount: 0, framesBefore: 0, frameThreshold: 6, endOfStream: false));

    [Theory]
    [InlineData(24, 22)]
    [InlineData(12, 10)]
    [InlineData(6, 4)]
    [InlineData(4, 2)]
    [InlineData(3, 2)]  // clamped to minimum of 2
    [InlineData(2, 2)]  // clamped to minimum of 2
    [InlineData(1, 2)]  // clamped to minimum of 2
    [InlineData(0, 2)]  // clamped to minimum of 2
    public void GetTopUpThreshold_LeavesTwoFramesHeadroom_ClampedAtTwo(int ceiling, int expected) =>
        Assert.Equal(expected, SeamlessLoopRestart.GetTopUpThreshold(ceiling));
}
