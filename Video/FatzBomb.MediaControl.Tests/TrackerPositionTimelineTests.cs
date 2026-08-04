using FatzBomb.MediaControl.Core.Audio;

namespace FatzBomb.MediaControl.Tests;

public sealed class TrackerPositionTimelineTests
{
    [Fact]
    public void Resolve_AfterAuthoredBackwardJump_ReturnsFirstPassPosition()
    {
        var timeline = new TrackerPositionTimeline();

        _ = timeline.Resolve(order: 0, row: 0, bytePositionSeconds: 0, looping: true);
        _ = timeline.Resolve(order: 1, row: 0, bytePositionSeconds: 10, looping: true);
        _ = timeline.Resolve(order: 1, row: 32, bytePositionSeconds: 15, looping: true);
        _ = timeline.Resolve(order: 2, row: 0, bytePositionSeconds: 20, looping: true);

        double loopPosition = timeline.Resolve(order: 1, row: 0, bytePositionSeconds: 20, looping: true);

        Assert.Equal(10, loopPosition);
    }

    [Fact]
    public void Resolve_AfterAuthoredBackwardJump_InterpolatesUnsampledRows()
    {
        var timeline = new TrackerPositionTimeline();

        _ = timeline.Resolve(order: 3, row: 0, bytePositionSeconds: 30, looping: true);
        _ = timeline.Resolve(order: 3, row: 32, bytePositionSeconds: 34, looping: true);
        _ = timeline.Resolve(order: 4, row: 0, bytePositionSeconds: 40, looping: true);
        _ = timeline.Resolve(order: 3, row: 0, bytePositionSeconds: 40, looping: true);

        double loopPosition = timeline.Resolve(order: 3, row: 16, bytePositionSeconds: 40, looping: true);

        Assert.Equal(32, loopPosition);
    }

    [Fact]
    public void Reset_DiscardsPriorLoopMapping()
    {
        var timeline = new TrackerPositionTimeline();

        _ = timeline.Resolve(order: 1, row: 0, bytePositionSeconds: 10, looping: true);
        _ = timeline.Resolve(order: 2, row: 0, bytePositionSeconds: 20, looping: true);
        _ = timeline.Resolve(order: 1, row: 0, bytePositionSeconds: 20, looping: true);
        timeline.Reset();

        double position = timeline.Resolve(order: 1, row: 0, bytePositionSeconds: 55, looping: true);

        Assert.Equal(55, position);
    }

    [Fact]
    public void Resolve_BackwardJumpWithoutLooping_StartsANewTimeline()
    {
        var timeline = new TrackerPositionTimeline();

        _ = timeline.Resolve(order: 1, row: 0, bytePositionSeconds: 10, looping: false);
        _ = timeline.Resolve(order: 2, row: 0, bytePositionSeconds: 20, looping: false);

        double position = timeline.Resolve(order: 1, row: 0, bytePositionSeconds: 30, looping: false);

        Assert.Equal(30, position);
    }

    [Fact]
    public void Resolve_RepeatedAuthoredLoops_ContinueUsingFirstPassTimeline()
    {
        var timeline = new TrackerPositionTimeline();

        _ = timeline.Resolve(order: 1, row: 0, bytePositionSeconds: 10, looping: true);
        _ = timeline.Resolve(order: 2, row: 0, bytePositionSeconds: 20, looping: true);
        _ = timeline.Resolve(order: 1, row: 0, bytePositionSeconds: 20, looping: true);
        _ = timeline.Resolve(order: 2, row: 0, bytePositionSeconds: 20, looping: true);

        double position = timeline.Resolve(order: 1, row: 0, bytePositionSeconds: 20, looping: true);

        Assert.Equal(10, position);
    }

    [Fact]
    public void Resolve_UnseenLoopTargetOrder_UsesBytePositionFallback()
    {
        var timeline = new TrackerPositionTimeline();

        _ = timeline.Resolve(order: 2, row: 0, bytePositionSeconds: 20, looping: true);
        _ = timeline.Resolve(order: 3, row: 0, bytePositionSeconds: 30, looping: true);

        double position = timeline.Resolve(order: 1, row: 0, bytePositionSeconds: 30, looping: true);

        Assert.Equal(30, position);
    }
}
