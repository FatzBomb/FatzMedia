namespace FatzBomb.MediaControl.Wpf;

internal enum SurfaceAutoHideState
{
    Idle,
    PendingShow,
    Shown,
    Hidden
}

internal sealed class SurfaceAutoHidePolicy
{
    public SurfaceAutoHideState State { get; private set; }

    public bool IsContentVisible => State == SurfaceAutoHideState.Shown;

    public bool MediaLoaded() => TransitionTo(SurfaceAutoHideState.PendingShow);

    public bool FramePresented() => State == SurfaceAutoHideState.PendingShow
        && TransitionTo(SurfaceAutoHideState.Shown);

    public bool MediaStarted() => State == SurfaceAutoHideState.Hidden
        && TransitionTo(SurfaceAutoHideState.PendingShow);

    public bool MediaPaused() => false;

    public bool MediaLooped() => State == SurfaceAutoHideState.Hidden
        && TransitionTo(SurfaceAutoHideState.PendingShow);

    public bool MediaStopped() => TransitionTo(SurfaceAutoHideState.Hidden);

    public bool MediaClosed() => TransitionTo(SurfaceAutoHideState.Hidden);

    public bool MediaError() => TransitionTo(SurfaceAutoHideState.Hidden);

    public bool MediaEnded(bool isLooping) => !isLooping
        && TransitionTo(SurfaceAutoHideState.Hidden);

    public bool Reset() => TransitionTo(SurfaceAutoHideState.Idle);

    private bool TransitionTo(SurfaceAutoHideState state)
    {
        var wasVisible = IsContentVisible;
        State = state;
        return wasVisible != IsContentVisible;
    }
}
