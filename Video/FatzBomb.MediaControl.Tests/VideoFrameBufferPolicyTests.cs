using FatzBomb.MediaControl.Core.Video;
using FatzBomb.MediaControl.Pipeline;

namespace FatzBomb.MediaControl.Tests;

public class VideoFrameBufferPolicyTests
{
    [Theory]
    [InlineData(3840, 2160, 60.0, 4)] // 33 MB BGRA frames: memory bound clamps to the minimum
    [InlineData(3840, 2160, 23.976, 4)]
    [InlineData(1920, 1080, 60.0, 12)] // 96 MiB / 8.3 MB
    [InlineData(1920, 1080, 24.0, 6)] // a quarter second
    [InlineData(1280, 720, 60.0, 12)]
    [InlineData(1280, 720, 0.0, 10)] // unknown rate
    public void TargetBufferedFrames_MatchesThePipelineFormula(int width, int height, double fps, int expected)
    {
        Assert.Equal(expected, VideoFrameBufferPolicy.GetTargetBufferedFrames(width, height, fps));
    }

    [Theory]
    [InlineData(4, 8)] // 4K: the floor
    [InlineData(8, 8)]
    [InlineData(11, 11)] // above the floor: the pipeline's target
    [InlineData(12, 12)]
    public void HardwareQueueCapacity_IsTheTargetWithAFloorOfEight(int target, int expected)
    {
        Assert.Equal(expected, VideoFrameBufferPolicy.GetHardwareQueueCapacity(target, maxQueueFrames: 24));
    }

    [Fact]
    public void HardwareQueueCapacity_NeverExceedsTheDecoderCap()
    {
        Assert.Equal(6, VideoFrameBufferPolicy.GetHardwareQueueCapacity(12, maxQueueFrames: 6));
        Assert.Equal(1, VideoFrameBufferPolicy.GetHardwareQueueCapacity(12, maxQueueFrames: 0));
    }

    [Theory]
    [InlineData(8, 10)]
    [InlineData(12, 14)]
    [InlineData(0, 2)]
    [InlineData(-5, 2)]
    public void SurfaceReserve_IsTheQueueCapPlusThePresentationHold(int queueCapacity, int expected)
    {
        Assert.Equal(expected, VideoFrameBufferPolicy.GetHardwareSurfaceReserve(queueCapacity));
    }

    [Fact]
    public void SurfaceReserve_At4KIsAtMostTenAndNeverAboveFourteen()
    {
        var reserve4K = VideoFrameBufferPolicy.GetHardwareSurfaceReserve(
            VideoFrameBufferPolicy.GetHardwareQueueCapacity(VideoFrameBufferPolicy.GetTargetBufferedFrames(3840, 2160, 60), 24));
        Assert.Equal(10, reserve4K);

        var largest = VideoFrameBufferPolicy.GetHardwareSurfaceReserve(
            VideoFrameBufferPolicy.GetHardwareQueueCapacity(VideoFrameBufferPolicy.MaxBufferedFrames, 24));
        Assert.Equal(14, largest);
    }

    [Theory]
    [InlineData(1.0, 1.04, 5.0, true)] // both before the target: the successor is closer
    [InlineData(4.96, 5.0, 5.0, true)] // successor exactly on target
    [InlineData(4.96, 5.04, 5.0, false)] // successor past the target: the head may be the closer one
    [InlineData(5.0, 5.04, 5.0, false)] // head on target
    [InlineData(6.0, 6.04, 5.0, false)] // both after the target
    [InlineData(1.04, 1.0, 5.0, false)] // out of order: keep
    public void SeekMayDropOnlyAFrameItsSuccessorSupersedes(double head, double next, double target, bool expected)
    {
        Assert.Equal(expected, VideoSeekPolicy.CanDropQueuedSeekFrame(head, next, target));
    }
}
