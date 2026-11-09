#pragma warning disable IDE0130
namespace FatzMedia.Video;

enum SurfaceAutoHideState
{
    Idle,
    PendingShow,
    Shown,
    Hidden
}

/// <summary>
/// The surface auto-hide states (Stage 029C, in Core since 046A). Pure and not thread-safe:
/// <see cref="MediaSurfaceContentTracker"/> drives it under its lock.
/// </summary>
sealed class SurfaceAutoHidePolicy
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

    bool TransitionTo(SurfaceAutoHideState state)
    {
        var wasVisible = IsContentVisible;
        State = state;
        return wasVisible != IsContentVisible;
    }
}
