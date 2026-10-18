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

    /// <summary>
    /// Why the last <see cref="PresentComposition"/> returned false (for example "no active
    /// presentation target"), or null. Quoted in the host-visible failure diagnostic.
    /// </summary>
    string? LastPresentFailureReason => null;

    /// <summary>
    /// Refresh rate in Hz of the display currently showing <paramref name="surface"/>, or null
    /// when the backend cannot tell (no native window, or the surface is not attached). Core
    /// prefers it over the platform's global rate for video timing (Stage 017A). Called from
    /// presentation threads, so implementations return a cached value and refresh it off the
    /// caller's thread.
    /// </summary>
    double? GetSurfaceRefreshRateHz(MediaPresentationSurfaceDescriptor surface) => null;

    /// <summary>
    /// Vertical-blank timing of the display showing <paramref name="surface"/> (Stage 017A slice
    /// 4), for <see cref="VideoFrameTiming.DisplayLocked"/> video timing. Returns false when the
    /// backend cannot tell (no native window, not attached, or the display's timing is not
    /// available yet); Core then keeps source-cadence timing. Called from the video presentation
    /// thread once per display refresh, so implementations return cached data.
    /// </summary>
    bool TryGetSurfaceVsyncTiming(MediaPresentationSurfaceDescriptor surface, out PresentationVsyncTiming timing)
    {
        timing = default;
        return false;
    }

    void CleanupResources();

    bool EnsureNativeFrameDevice(VideoFrameEventArgs frame, out bool deviceAdopted);
}
