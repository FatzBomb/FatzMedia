using FatzBomb.MediaControl.Core.Cdg;

namespace FatzBomb.MediaControl.Tests;

public class CdgDirtyRectTests
{
    [Fact]
    public void Empty_IsEmptyWithZeroDimensions()
    {
        CdgDirtyRect rect = CdgDirtyRect.Empty;

        Assert.True(rect.IsEmpty);
        Assert.Equal(0, rect.Width);
        Assert.Equal(0, rect.Height);
    }

    [Fact]
    public void FullFrame_CoversEntireCdgFrame()
    {
        CdgDirtyRect rect = CdgDirtyRect.FullFrame;

        Assert.False(rect.IsEmpty);
        Assert.Equal(0, rect.X);
        Assert.Equal(0, rect.Y);
        Assert.Equal(CdgConstants.FrameWidth, rect.Width);
        Assert.Equal(CdgConstants.FrameHeight, rect.Height);
    }

    [Fact]
    public void ClampToFrame_ReturnsRectUnchangedWhenInsideFrame()
    {
        var rect = new CdgDirtyRect(10, 20, 30, 40);

        CdgDirtyRect clamped = rect.ClampToFrame();

        Assert.Equal(rect, clamped);
    }

    [Fact]
    public void ClampToFrame_ClampsToFrameBounds()
    {
        var rect = new CdgDirtyRect(-5, -10, CdgConstants.FrameWidth + 20, CdgConstants.FrameHeight + 30);

        CdgDirtyRect clamped = rect.ClampToFrame();

        Assert.Equal(0, clamped.X);
        Assert.Equal(0, clamped.Y);
        Assert.Equal(CdgConstants.FrameWidth, clamped.Width);
        Assert.Equal(CdgConstants.FrameHeight, clamped.Height);
    }

    [Fact]
    public void ClampToFrame_ReturnsEmptyWhenFullyOutsideFrame()
    {
        var rect = new CdgDirtyRect(
            CdgConstants.FrameWidth + 10,
            CdgConstants.FrameHeight + 10,
            5, 5);

        CdgDirtyRect clamped = rect.ClampToFrame();

        Assert.Equal(CdgDirtyRect.Empty, clamped);
        Assert.True(clamped.IsEmpty);
    }

    [Fact]
    public void ClampToFrame_ReturnsEmptyWhenZeroAreaAfterClamp()
    {
        var rect = new CdgDirtyRect(CdgConstants.FrameWidth, 0, 10, 10);

        CdgDirtyRect clamped = rect.ClampToFrame();

        Assert.True(clamped.IsEmpty);
    }
}
