using FatzBomb.MediaControl.Core.Abstractions;

namespace FatzBomb.MediaControl.Core.Graphics.Composition;

public interface IMediaRendererBackend : IDisposable
{
    event EventHandler? FramePresented;

    string BackendName { get; }
    string DeviceLostDiagnosticCategory { get; }
    string DeviceLostRecoveryFailedDiagnosticCategory { get; }
    bool IsInitialized { get; }
    nint NativeDeviceHandle { get; }

    bool Initialize();
    bool AttachSurface(MediaPresentationSurfaceDescriptor surface);
    bool ResizeSurface(MediaPresentationSurfaceDescriptor surface);
    bool SetSurfaceLiveResize(MediaPresentationSurfaceDescriptor surface, bool isResizing);
    bool DetachSurface(MediaPresentationSurfaceDescriptor surface);

    void SetPresentationStretch(MediaCompositionStretchMode stretchMode);

    bool PresentNativeFrame(VideoFrameEventArgs frame);
    void ClearCompositionBackgroundFrame();
    bool PresentComposition(MediaCompositionFrame frame);

    void CleanupResources();

    bool EnsureNativeFrameDevice(VideoFrameEventArgs frame, out bool deviceAdopted);
}
