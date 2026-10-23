namespace FatzBomb.MediaControl.Controllers;

/// <summary>
/// The presentation coordinator of an Audio-mode player (Stage 043A): no renderer, GPU device or
/// surfaces. Surface attach calls return <see langword="false"/> with a diagnostic; everything
/// else does nothing.
/// </summary>
internal sealed class AudioOnlyPresentationCoordinator : IMediaPresentationCoordinator
{
    public event EventHandler? FramePresented { add { } remove { } }

    public VideoDisplayMode VideoDisplayMode { get; set; }

    public BaseVisualTransform BaseVisualTransform { get; set; }

    public bool AttachPresentationSurface(MediaPresentationSurfaceDescriptor surface)
    {
        DiagnosticRouter.Raise(
            MediaDiagnosticLevel.Warning,
            MediaPlayerModePolicy.DiagnosticCategory,
            $"Audio mode has no presentation; surface '{surface?.Id}' was not attached.");
        return false;
    }

    public bool ResizePresentationSurface(string surfaceId, int pixelWidth, int pixelHeight) => false;

    public bool SetPresentationSurfaceLiveResize(string surfaceId, bool isResizing) => false;

    public bool DetachPresentationSurface(string surfaceId) => false;

    public void RefreshCurrentPosition()
    {
    }

    public void RunOnRenderThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    public void StartRendering()
    {
    }

    public void StopRendering()
    {
    }

    public void RefreshAttachments()
    {
    }

    public void Dispose()
    {
    }
}
