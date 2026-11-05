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

    /// <summary>
    /// Whether the renderer's device (including a decoder device it adopted or shares) is lost,
    /// with <paramref name="reason"/> saying how it was found (ENG-039). Core calls it about once a
    /// second while the renderer is initialized and right after a failed present or surface call,
    /// and recovers the device (<see cref="CleanupResources"/>, then <see cref="Initialize"/>) when
    /// it returns true: a loss is detected even when no frame reaches a present (paused, CD+G
    /// only, or a path that fails before presenting). Must be cheap and thread-safe, and must not
    /// take the renderer's present lock or wait for the GPU without a short bound. A renderer
    /// that cannot tell returns false (the default).
    /// </summary>
    bool ProbeDeviceLost(out string? reason)
    {
        reason = null;
        return false;
    }

    /// <summary>
    /// Whether another device-recovery attempt can succeed (ENG-050). False when the platform has
    /// refused the renderer's API for the rest of the process (after an NVIDIA GPU reset, every
    /// <c>vkCreateInstance</c> fails with <c>VK_ERROR_INCOMPATIBLE_DRIVER</c>): Core then stops
    /// the attempts and reports the failure at once, so the host can switch renderers. The default
    /// is true.
    /// </summary>
    bool CanRetryDeviceRecovery => true;
}
