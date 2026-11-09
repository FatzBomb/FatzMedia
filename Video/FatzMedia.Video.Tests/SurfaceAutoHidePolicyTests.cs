namespace FatzMedia.Video.Tests;

/// <summary>The auto-hide states, in Core since Stage 046A (portable).</summary>
public sealed class SurfaceAutoHidePolicyTests
{
    [Fact]
    public void LoadRequiresPresentedFrameBeforeShowing()
    {
        var policy = new SurfaceAutoHidePolicy();

        Assert.False(policy.MediaLoaded());
        Assert.Equal(SurfaceAutoHideState.PendingShow, policy.State);
        Assert.False(policy.IsContentVisible);

        Assert.True(policy.FramePresented());
        Assert.Equal(SurfaceAutoHideState.Shown, policy.State);
        Assert.True(policy.IsContentVisible);
        Assert.False(policy.FramePresented());
    }

    [Fact]
    public void ExplicitStopCloseAndErrorHideShownContent()
    {
        var policy = CreateShownPolicy();
        Assert.True(policy.MediaStopped());
        Assert.False(policy.IsContentVisible);

        _ = policy.MediaLoaded();
        _ = policy.FramePresented();
        Assert.True(policy.MediaClosed());
        Assert.False(policy.IsContentVisible);

        _ = policy.MediaLoaded();
        _ = policy.FramePresented();
        Assert.True(policy.MediaError());
        Assert.False(policy.IsContentVisible);
    }

    [Fact]
    public void PauseAndLoopSignalsKeepShownContentVisible()
    {
        var policy = CreateShownPolicy();

        Assert.False(policy.MediaStarted());
        Assert.False(policy.MediaPaused());
        Assert.False(policy.MediaEnded(isLooping: true));
        Assert.False(policy.MediaLooped());
        Assert.True(policy.IsContentVisible);
    }

    [Fact]
    public void PlayAfterExplicitStopRearmsUntilNextPresentedFrame()
    {
        var policy = CreateShownPolicy();
        _ = policy.MediaStopped();

        Assert.False(policy.MediaStarted());
        Assert.Equal(SurfaceAutoHideState.PendingShow, policy.State);
        Assert.False(policy.IsContentVisible);

        Assert.True(policy.FramePresented());
        Assert.True(policy.IsContentVisible);
    }

    [Fact]
    public void NonLoopNaturalEndHidesShownContent()
    {
        var policy = CreateShownPolicy();

        Assert.True(policy.MediaEnded(isLooping: false));

        Assert.Equal(SurfaceAutoHideState.Hidden, policy.State);
        Assert.False(policy.IsContentVisible);
    }

    [Fact]
    public void ReplacementLoadRearmsAfterStopOrShownContent()
    {
        var policy = CreateShownPolicy();
        _ = policy.MediaStopped();

        Assert.False(policy.MediaLoaded());
        Assert.Equal(SurfaceAutoHideState.PendingShow, policy.State);

        _ = policy.FramePresented();
        Assert.True(policy.IsContentVisible);
        Assert.True(policy.MediaLoaded());
        Assert.Equal(SurfaceAutoHideState.PendingShow, policy.State);
        Assert.False(policy.IsContentVisible);
    }

    [Fact]
    public void FrameBeforeLoadDoesNotShowWithoutAdapterReplay()
    {
        var policy = new SurfaceAutoHidePolicy();

        Assert.False(policy.FramePresented());
        Assert.False(policy.MediaLoaded());

        Assert.Equal(SurfaceAutoHideState.PendingShow, policy.State);
        Assert.False(policy.IsContentVisible);
    }

    static SurfaceAutoHidePolicy CreateShownPolicy()
    {
        var policy = new SurfaceAutoHidePolicy();
        _ = policy.MediaLoaded();
        _ = policy.FramePresented();
        return policy;
    }
}
