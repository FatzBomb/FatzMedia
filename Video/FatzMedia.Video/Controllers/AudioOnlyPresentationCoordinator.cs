namespace FatzMedia.Video.Controllers;

/// <summary>
/// The presentation coordinator of an Audio-mode player (Stage 043A): no renderer, GPU device or
/// surfaces. Surfaces the host attaches are accepted and only remembered (with a diagnostic), so
/// the player keeps them, at their latest size, for a switch to Full or Background mode
/// (slice 2); everything else does nothing.
/// </summary>
sealed class AudioOnlyPresentationCoordinator : IMediaPresentationCoordinator
{
    readonly HashSet<string> _surfaceIds = new(StringComparer.OrdinalIgnoreCase);
    readonly Lock _gate = new();

    public event EventHandler? FramePresented { add { } remove { } }

    public VideoDisplayMode VideoDisplayMode { get; set; }

    public BaseVisualTransform BaseVisualTransform { get; set; }

    public bool AttachPresentationSurface(MediaPresentationSurfaceDescriptor surface)
    {
        if (surface is null || !surface.IsValid)
            return false;

        lock (_gate)
            _ = _surfaceIds.Add(surface.Id);

        DiagnosticRouter.Raise(
            MediaDiagnosticLevel.Info,
            MediaPlayerModePolicy.DiagnosticCategory,
            $"Audio mode has no presentation; surface '{surface.Id}' is kept for a switch to Full or Background mode.");
        return true;
    }

    public bool ResizePresentationSurface(string surfaceId, int pixelWidth, int pixelHeight) => HasSurface(surfaceId);

    public bool SetPresentationSurfaceLiveResize(string surfaceId, bool isResizing) => HasSurface(surfaceId);

    public bool DetachPresentationSurface(string surfaceId)
    {
        lock (_gate)
            return _surfaceIds.Remove(surfaceId);
    }

    bool HasSurface(string surfaceId)
    {
        lock (_gate)
            return _surfaceIds.Contains(surfaceId);
    }

    public void RefreshCurrentPosition()
    {
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
