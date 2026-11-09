using FatzMedia.Video.Core.Audio;

namespace FatzMedia.Video.Tests;

public sealed class TrackerPositionTimelineTests
{
    [Fact]
    public void Resolve_AfterAuthoredBackwardJump_ReturnsFirstPassPosition()
    {
        var timeline = new TrackerPositionTimeline();

        _ = timeline.Resolve(order: 0, row: 0, bytePositionSeconds: 0);
        _ = timeline.Resolve(order: 1, row: 0, bytePositionSeconds: 10);
        _ = timeline.Resolve(order: 1, row: 32, bytePositionSeconds: 15);
        _ = timeline.Resolve(order: 2, row: 0, bytePositionSeconds: 20);

        double loopPosition = timeline.Resolve(order: 1, row: 0, bytePositionSeconds: 20);

        Assert.Equal(10, loopPosition);
    }

    [Fact]
    public void Resolve_AfterAuthoredBackwardJump_InterpolatesUnsampledRows()
    {
        var timeline = new TrackerPositionTimeline();

        _ = timeline.Resolve(order: 3, row: 0, bytePositionSeconds: 30);
        _ = timeline.Resolve(order: 3, row: 32, bytePositionSeconds: 34);
        _ = timeline.Resolve(order: 4, row: 0, bytePositionSeconds: 40);
        _ = timeline.Resolve(order: 3, row: 0, bytePositionSeconds: 40);

        double loopPosition = timeline.Resolve(order: 3, row: 16, bytePositionSeconds: 40);

        Assert.Equal(32, loopPosition);
    }

    [Fact]
    public void Reset_DiscardsPriorLoopMapping()
    {
        var timeline = new TrackerPositionTimeline();

        _ = timeline.Resolve(order: 1, row: 0, bytePositionSeconds: 10);
        _ = timeline.Resolve(order: 2, row: 0, bytePositionSeconds: 20);
        _ = timeline.Resolve(order: 1, row: 0, bytePositionSeconds: 20);
        timeline.Reset();

        double position = timeline.Resolve(order: 1, row: 0, bytePositionSeconds: 55);

        Assert.Equal(55, position);
    }

    [Fact]
    public void Resolve_AuthoredBackwardJumpWithoutHostLoop_UsesFirstPassTimeline()
    {
        var timeline = new TrackerPositionTimeline();

        _ = timeline.Resolve(order: 1, row: 0, bytePositionSeconds: 10);
        _ = timeline.Resolve(order: 2, row: 0, bytePositionSeconds: 20);

        double position = timeline.Resolve(order: 1, row: 0, bytePositionSeconds: 30);

        Assert.Equal(10, position);
    }

    [Fact]
    public void Resolve_RepeatedAuthoredLoops_ContinueUsingFirstPassTimeline()
    {
        var timeline = new TrackerPositionTimeline();

        _ = timeline.Resolve(order: 1, row: 0, bytePositionSeconds: 10);
        _ = timeline.Resolve(order: 2, row: 0, bytePositionSeconds: 20);
        _ = timeline.Resolve(order: 1, row: 0, bytePositionSeconds: 20);
        _ = timeline.Resolve(order: 2, row: 0, bytePositionSeconds: 20);

        double position = timeline.Resolve(order: 1, row: 0, bytePositionSeconds: 20);

        Assert.Equal(10, position);
    }

    [Fact]
    public void Resolve_UnseenLoopTargetOrder_UsesBytePositionFallback()
    {
        var timeline = new TrackerPositionTimeline();

        _ = timeline.Resolve(order: 2, row: 0, bytePositionSeconds: 20);
        _ = timeline.Resolve(order: 3, row: 0, bytePositionSeconds: 30);

        double position = timeline.Resolve(order: 1, row: 0, bytePositionSeconds: 30);

        Assert.Equal(30, position);
    }
}
