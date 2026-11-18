using FatzMedia.Video.WebView2;

namespace FatzMedia.Video.Tests;

/// <summary>Stage 060A: the double-click decision behind the web view's host-side double-click event.</summary>
public sealed class WebView2DoubleClickDetectorTests
{
    const uint Max = 500;

    [Fact]
    public void TwoQuickPressesAtTheSamePlace_AreADoubleClick()
    {
        var d = new DoubleClickDetector();

        Assert.False(d.Press(100, 100, 1000, Max, 2, 2));
        Assert.True(d.Press(101, 100, 1200, Max, 2, 2));
    }

    [Fact]
    public void ASlowSecondPress_IsNotADoubleClick_ButStartsANewPair()
    {
        var d = new DoubleClickDetector();

        Assert.False(d.Press(100, 100, 1000, Max, 2, 2));
        Assert.False(d.Press(100, 100, 1600, Max, 2, 2));
        Assert.True(d.Press(100, 100, 1800, Max, 2, 2));
    }

    [Fact]
    public void ASecondPressTooFarAway_IsNotADoubleClick()
    {
        var d = new DoubleClickDetector();

        Assert.False(d.Press(100, 100, 1000, Max, 2, 2));
        Assert.False(d.Press(120, 100, 1100, Max, 2, 2));
    }

    [Fact]
    public void AThirdQuickPress_StartsANewPair_SoTwoDoubleClicksNeedFourPresses()
    {
        var d = new DoubleClickDetector();

        Assert.False(d.Press(10, 10, 1000, Max, 2, 2));
        Assert.True(d.Press(10, 10, 1100, Max, 2, 2));
        Assert.False(d.Press(10, 10, 1200, Max, 2, 2));
        Assert.True(d.Press(10, 10, 1300, Max, 2, 2));
    }

    [Fact]
    public void Reset_ForgetsThePreviousPress()
    {
        var d = new DoubleClickDetector();
        Assert.False(d.Press(10, 10, 1000, Max, 2, 2));

        d.Reset();

        Assert.False(d.Press(10, 10, 1100, Max, 2, 2));
    }

    [Fact]
    public void TheTimerWrappingAround_StillCountsAsQuick()
    {
        var d = new DoubleClickDetector();

        Assert.False(d.Press(10, 10, uint.MaxValue - 100, Max, 2, 2));
        Assert.True(d.Press(10, 10, 100, Max, 2, 2));
    }
}
