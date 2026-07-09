using FatzBombMedia.MediaControl.Core.Audio;

namespace FatzBombMedia.MediaControl.Tests;

public class AudioLoopBoundaryTrackerTests
{
    [Fact]
    public void Advance_NoBoundaries_ReturnsZeroCrossed()
    {
        var boundaries = new Queue<double>();
        double offset = 0;

        var crossed = AudioLoopBoundaryTracker.Advance(10.0, boundaries, ref offset);

        Assert.Equal(0, crossed);
        Assert.Equal(0, offset);
    }

    [Fact]
    public void Advance_PositionBeforeBoundary_DoesNotCross()
    {
        var boundaries = new Queue<double>([120.0]);
        double offset = 0;

        var crossed = AudioLoopBoundaryTracker.Advance(119.9, boundaries, ref offset);

        Assert.Equal(0, crossed);
        Assert.Equal(0, offset);
        _ = Assert.Single(boundaries);
    }

    [Fact]
    public void Advance_PositionAtBoundary_CrossesOnce()
    {
        var boundaries = new Queue<double>([120.0]);
        double offset = 0;

        var crossed = AudioLoopBoundaryTracker.Advance(120.0, boundaries, ref offset);

        Assert.Equal(1, crossed);
        Assert.Equal(120.0, offset);
        Assert.Empty(boundaries);
    }

    [Fact]
    public void Advance_PositionPastMultipleBoundaries_CrossesAllAndKeepsLatestOffset()
    {
        var boundaries = new Queue<double>([120.0, 240.0, 360.0]);
        double offset = 0;

        var crossed = AudioLoopBoundaryTracker.Advance(250.0, boundaries, ref offset);

        Assert.Equal(2, crossed);
        Assert.Equal(240.0, offset);
        _ = Assert.Single(boundaries); // 360.0 remains, not yet reached
    }

    [Fact]
    public void Advance_CalledRepeatedly_AccumulatesAcrossCalls()
    {
        var boundaries = new Queue<double>([120.0, 240.0]);
        double offset = 0;

        var crossed1 = AudioLoopBoundaryTracker.Advance(100.0, boundaries, ref offset);
        Assert.Equal(0, crossed1);
        Assert.Equal(0, offset);
        Assert.Equal(2, boundaries.Count);

        var crossed2 = AudioLoopBoundaryTracker.Advance(121.0, boundaries, ref offset);
        Assert.Equal(1, crossed2);
        Assert.Equal(120.0, offset);
        _ = Assert.Single(boundaries);

        var crossed3 = AudioLoopBoundaryTracker.Advance(241.0, boundaries, ref offset);
        Assert.Equal(1, crossed3);
        Assert.Equal(240.0, offset);
        Assert.Empty(boundaries);
    }

    [Fact]
    public void Advance_ExactlyAtEachBoundary_CrossesInOrder()
    {
        var boundaries = new Queue<double>([50.0, 100.0]);
        double offset = 0;

        var crossed1 = AudioLoopBoundaryTracker.Advance(50.0, boundaries, ref offset);
        Assert.Equal(1, crossed1);
        Assert.Equal(50.0, offset);

        var crossed2 = AudioLoopBoundaryTracker.Advance(100.0, boundaries, ref offset);
        Assert.Equal(1, crossed2);
        Assert.Equal(100.0, offset);
        Assert.Empty(boundaries);
    }

    [Fact]
    public void Advance_NullQueue_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
        {
            double offset = 0;
            _ = AudioLoopBoundaryTracker.Advance(1.0, null!, ref offset);
        });
}
