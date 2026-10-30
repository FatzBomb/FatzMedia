namespace FatzBomb.MediaControl.Controllers;

/// <summary>
/// Platform presentation owner for a media session. Implementations create native
/// presentation targets and render the platform-specific final composition; Core
/// owns session lifecycle and supplies framework-neutral surface descriptors.
/// </summary>
public interface IMediaPresentationCoordinator : IMediaRenderCoordinator, IDisposable
{
    event EventHandler? FramePresented;

    VideoDisplayMode VideoDisplayMode { get; set; }
    BaseVisualTransform BaseVisualTransform { get; set; }

    bool AttachPresentationSurface(MediaPresentationSurfaceDescriptor surface);
    bool ResizePresentationSurface(string surfaceId, int pixelWidth, int pixelHeight);
    bool SetPresentationSurfaceLiveResize(string surfaceId, bool isResizing);
    bool DetachPresentationSurface(string surfaceId);
    void RefreshCurrentPosition();

    /// <summary>
    /// Raised after the renderer's device was lost and recovered (ENG-039). Video decoded on the
    /// lost device is gone, so the player reopens it. Coordinators without device recovery never
    /// raise it.
    /// </summary>
    event EventHandler? DeviceRecovered
    {
        add { }
        remove { }
    }
}
