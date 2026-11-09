using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Graphics;
using FatzMedia.Video.Core.Graphics.Composition;
using FatzMedia.Video.Core.Graphics.D3D.Device;
using FatzMedia.Video.Core.Graphics.D3D.Diagnostics;
using FatzMedia.Video.Core.Graphics.D3D.Presentation;
using System.Diagnostics;

namespace FatzMedia.Video.Direct3D11;

internal sealed class NativeD3D11PresentationBridge : IMediaRendererBackend
{
    private readonly D3D11DeviceResources _deviceResources;
    private readonly D3D11RendererPresentationServices _presentation;
    private readonly Lock _lock = new();
    private readonly MediaPlayerConfiguration _configuration;
    private readonly MonitorRefreshRateCache _refreshRates = new(Win32MonitorRefreshRate.TryGet);
    private readonly MonitorVblankClocks _vblankClocks = new();
    private bool _disposed;
    private bool _videoSupportRefusalReported;

    public NativeD3D11PresentationBridge(
        MediaPlayerConfiguration configuration,
        Direct3D11PresentationOptions options)
    {
        _configuration = configuration;
        _deviceResources = new D3D11DeviceResources(options.DeviceSelection);
        _presentation = new D3D11RendererPresentationServices(
            configuration,
            options,
            _deviceResources);
    }

    public event EventHandler? FramePresented;

    /// <summary>Gets the presentation services (tests).</summary>
    internal D3D11RendererPresentationServices PresentationServices => _presentation;

    public nint NativeDeviceHandle => _deviceResources.NativeDevicePointer;

    public string BackendName => _presentation.PresentationBackend;

    public string DeviceLostDiagnosticCategory => "D3D11.DeviceLost";

    public string DeviceLostRecoveryFailedDiagnosticCategory => "D3D11.DeviceLost.RecoveryFailed";

    public bool IsInitialized => NativeDeviceHandle != 0;

    /// <summary>
    /// ENG-039: reads the current device's removal reason (the device a decoder brought, once
    /// adopted, is the same device). It takes no presentation lock, so a present stuck in the
    /// driver does not hide the loss.
    /// </summary>
    public bool ProbeDeviceLost(out string? reason)
    {
        reason = null;
        if (Volatile.Read(ref _disposed) || !_deviceResources.ProbeDeviceRemoved(out SharpGen.Runtime.Result removed))
            return false;

        reason = $"Direct3D11 {D3D11DeviceLostClassifier.Describe(removed, swapChain: null)}";
        return true;
    }

    public bool Initialize()
    {
        if (_disposed)
        {
            return false;
        }

        lock (_lock)
        {
            return _presentation.InitializeDevice(_disposed);
        }
    }

    private bool InitializeFromNativeDevice(nint devicePointer, nint deviceContextPointer)
    {
        if (_disposed || devicePointer == IntPtr.Zero)
        {
            return false;
        }

        lock (_lock)
        {
            return _presentation.InitializeFromNativeDevice(_disposed, devicePointer, deviceContextPointer);
        }
    }

    private bool CreateNativeHwndSurface(nint hwnd, int width, int height)
    {
        if (_disposed)
        {
            return false;
        }

        lock (_lock)
        {
            return _presentation.CreateNativeHwndSurface(_disposed, hwnd, width, height, NotifyPresentedWithSync);
        }
    }

    private bool CreateDirectCompositionSurface(nint hwnd, int width, int height)
    {
        if (_disposed)
        {
            return false;
        }

        lock (_lock)
        {
            return _presentation.CreateDirectCompositionSurface(_disposed, hwnd, width, height, NotifyPresentedWithSync);
        }
    }

    private bool CreateSecondaryNativeHwndSurface(string targetId, nint hwnd, int width, int height)
    {
        if (_disposed)
        {
            return false;
        }

        lock (_lock)
        {
            return _presentation.CreateSecondaryNativeHwndSurface(_disposed, targetId, hwnd, width, height);
        }
    }

    private bool CreateManagedFrameSinkSurface(
        IMediaPresentationFrameSink sink,
        int width,
        int height)
    {
        if (_disposed)
            return false;

        lock (_lock)
        {
            return _presentation.CreateManagedFrameSinkSurface(
                _disposed,
                sink,
                width,
                height,
                NotifyPresentedWithSync);
        }
    }

    private bool CreateSharedTextureSurface(IMediaSharedGpuImageConsumer consumer, int width, int height)
    {
        if (_disposed)
            return false;

        bool created;
        string? reason;
        lock (_lock)
        {
            // The device must exist to export; Core initializes it before attaching.
            if (!_deviceResources.HasDevice)
            {
                created = false;
                reason = "no usable Direct3D11 device";
            }
            else
            {
                created = _presentation.CreateSharedTextureSurface(_disposed, consumer, width, height, NotifyPresentedWithSync, out reason);
            }
        }

        if (!created)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "D3D11", $"Shared textures unavailable: {reason}");
            consumer.OnUnavailable(reason ?? "the Direct3D11 renderer could not export shared textures");
        }

        return created;
    }

    private bool CreateSecondaryManagedFrameSinkSurface(string targetId, IMediaPresentationFrameSink sink, int width, int height)
    {
        if (_disposed)
            return false;

        lock (_lock)
            return _presentation.CreateSecondaryManagedFrameSinkSurface(_disposed, targetId, sink, width, height);
    }

    private bool CreateSecondarySharedTextureSurface(string targetId, IMediaSharedGpuImageConsumer consumer, int width, int height)
    {
        if (_disposed)
            return false;

        bool created;
        string? reason;
        lock (_lock)
            created = _presentation.CreateSecondarySharedTextureSurface(_disposed, targetId, consumer, width, height, out reason);

        if (!created)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "D3D11", $"Shared textures unavailable for '{targetId}': {reason}");
            consumer.OnUnavailable(reason ?? "the Direct3D11 renderer could not export shared textures");
        }

        return created;
    }

    private bool RemoveSecondaryPresentationTarget(string targetId)
    {
        lock (_lock)
        {
            return _presentation.RemoveSecondaryPresentationTarget(targetId);
        }
    }

    private void SetNativeLiveResizeInProgress(bool isResizing)
    {
        lock (_lock)
        {
            _presentation.SetNativeLiveResizeInProgress(isResizing);
        }
    }

    public void SetPresentationStretch(MediaCompositionStretchMode stretchMode)
    {
        lock (_lock)
        {
            _presentation.SetNativePresentationStretch(stretchMode);
        }
    }

    private void QueueNativeHwndPresentationResize(int width, int height)
    {
        lock (_lock)
        {
            _presentation.QueueNativeHwndResize(_disposed, width, height);
        }
    }

    private void QueueDirectCompositionPresentationResize(int width, int height)
    {
        lock (_lock)
        {
            _presentation.QueueDirectCompositionResize(_disposed, width, height);
        }
    }

    public bool AttachSurface(MediaPresentationSurfaceDescriptor surface)
    {
        if (_disposed)
            return false;

        if (TryAttachOffscreenSurface(surface, out var offscreenAttached))
            return offscreenAttached;

        if (!TryGetWin32Hwnd(surface, out nint hwnd))
            return false;

        var attached = surface.Role == MediaPresentationSurfaceRole.Primary
            ? CreateDirectCompositionSurface(hwnd, surface.PixelWidth, surface.PixelHeight)
                || CreateNativeHwndSurface(hwnd, surface.PixelWidth, surface.PixelHeight)
            : CreateSecondaryNativeHwndSurface(surface.Id, hwnd, surface.PixelWidth, surface.PixelHeight);
        // Every window's monitor rate is needed: the primary's for video timing, and each one's
        // as the nominal rate of its vertical-blank clock (display-locked timing on several
        // monitors). Attach also runs from the presentation thread when surfaces are recreated,
        // so this queries Windows only for a new window.
        if (attached)
        {
            _refreshRates.PrimeIfMissing(hwnd);
            // HDR output (Stage 042A slice 2) follows the primary window's monitor; a no-op
            // unless the option is on.
            if (surface.Role == MediaPresentationSurfaceRole.Primary)
                _presentation.HdrOutput.PrimeDisplay(hwnd);
        }

        return attached;
    }

    /// <summary>
    /// The refresh rate of the monitor showing a native-window surface (Stage 017A slice 3),
    /// cached and refreshed in the background so moving the window to another monitor is
    /// picked up within a second. Managed frame-sink surfaces have no monitor: null.
    /// </summary>
    public double? GetSurfaceRefreshRateHz(MediaPresentationSurfaceDescriptor surface) =>
        !Volatile.Read(ref _disposed) && TryGetWin32Hwnd(surface, out nint hwnd) ? _refreshRates.Get(hwnd) : null;

    /// <summary>
    /// The vertical-blank timing of the monitor showing a native-window surface (Stage 017A slice
    /// 4), from that monitor's own blanks. Managed frame-sink surfaces have no monitor: false.
    /// </summary>
    public bool TryGetSurfaceVsyncTiming(MediaPresentationSurfaceDescriptor surface, out PresentationVsyncTiming timing)
    {
        timing = default;
        return !Volatile.Read(ref _disposed)
            && TryGetWin32Hwnd(surface, out nint hwnd)
            && _vblankClocks.TryGet(hwnd, _refreshRates.Get(hwnd), out timing);
    }

    public bool ResizeSurface(MediaPresentationSurfaceDescriptor surface)
    {
        if (_disposed)
            return false;

        // Offscreen targets are re-created at the new size (attach and resize are the same).
        if (TryAttachOffscreenSurface(surface, out var offscreenResized))
            return offscreenResized;

        if (!TryGetWin32Hwnd(surface, out nint hwnd))
            return false;

        if (surface.Role != MediaPresentationSurfaceRole.Primary)
        {
            return CreateSecondaryNativeHwndSurface(
                surface.Id,
                hwnd,
                surface.PixelWidth,
                surface.PixelHeight);
        }

        if (_presentation.IsDirectCompositionActive)
        {
            QueueDirectCompositionPresentationResize(surface.PixelWidth, surface.PixelHeight);
        }
        else
        {
            QueueNativeHwndPresentationResize(surface.PixelWidth, surface.PixelHeight);
        }

        return true;
    }

    public bool SetSurfaceLiveResize(MediaPresentationSurfaceDescriptor surface, bool isResizing)
    {
        if (_disposed)
            return false;

        // Offscreen targets (any role) have no swap chain to hold back during a live resize.
        if (MediaPresentationFrameSinkHandle.TryResolve(surface, out _) || MediaSharedGpuImageConsumerHandle.TryResolve(surface, out _))
            return true;

        if (!TryGetWin32Hwnd(surface, out _))
            return false;

        if (surface.Role == MediaPresentationSurfaceRole.Primary)
        {
            SetNativeLiveResizeInProgress(isResizing);
        }

        return true;
    }

    /// <summary>
    /// Attaches or resizes a managed frame-sink or shared-texture surface: the primary as the
    /// offscreen primary backend, any other role as its own secondary target (Stage 027A slice
    /// 5). Returns false when the surface is of neither kind.
    /// </summary>
    private bool TryAttachOffscreenSurface(MediaPresentationSurfaceDescriptor surface, out bool attached)
    {
        var isPrimary = surface.Role == MediaPresentationSurfaceRole.Primary;
        if (MediaPresentationFrameSinkHandle.TryResolve(surface, out IMediaPresentationFrameSink? sink))
        {
            attached = isPrimary
                ? CreateManagedFrameSinkSurface(sink, surface.PixelWidth, surface.PixelHeight)
                : CreateSecondaryManagedFrameSinkSurface(surface.Id, sink, surface.PixelWidth, surface.PixelHeight);
            return true;
        }

        if (MediaSharedGpuImageConsumerHandle.TryResolve(surface, out IMediaSharedGpuImageConsumer? consumer))
        {
            attached = isPrimary
                ? CreateSharedTextureSurface(consumer!, surface.PixelWidth, surface.PixelHeight)
                : CreateSecondarySharedTextureSurface(surface.Id, consumer!, surface.PixelWidth, surface.PixelHeight);
            return true;
        }

        attached = false;
        return false;
    }

    private static bool TryGetWin32Hwnd(
        MediaPresentationSurfaceDescriptor? surface,
        out nint hwnd)
    {
        hwnd = 0;
        return surface is { IsValid: true }
            && surface.NativeSurface.SurfaceTypeId == MediaNativeSurfaceTypeIds.Win32Hwnd
            && surface.NativeSurface.TryGetHandle(MediaNativeHandleIds.Window, out hwnd);
    }

    public bool DetachSurface(MediaPresentationSurfaceDescriptor surface)
    {
        if (_disposed || surface is null)
        {
            return false;
        }

        if (TryGetWin32Hwnd(surface, out nint hwnd))
        {
            _refreshRates.Remove(hwnd);
            if (surface.Role == MediaPresentationSurfaceRole.Primary)
                _presentation.HdrOutput.ForgetDisplay(hwnd);
        }

        if (surface.Role != MediaPresentationSurfaceRole.Primary)
        {
            return RemoveSecondaryPresentationTarget(surface.Id);
        }

        DetachPrimarySurface();
        return true;
    }

    private void DetachPrimarySurface()
    {
        lock (_lock)
        {
            _presentation.CleanupPrimarySurfaces();
        }
    }

    public bool PresentNativeFrame(VideoFrameEventArgs frame)
    {
        if (frame?.NativeResource is not { IsValid: true } resource
            || resource.BackendId != MediaGraphicsBackendId.Direct3D11)
        {
            return false;
        }

        lock (_lock)
        {
            return _presentation.PresentHardwareFrame(
                _disposed,
                resource.ResourceHandle,
                resource.SubresourceIndex,
                frame.Width,
                frame.Height,
                frame.FramesPerSecond,
                resource.ColorSpace,
                resource.HdrToneMap,
                NotifyPresentedResult);
        }
    }

    public bool EnsureNativeFrameDevice(VideoFrameEventArgs frame, out bool deviceAdopted)
    {
        deviceAdopted = false;

        if (frame?.NativeResource is not { IsValid: true } resource
            || resource.BackendId != MediaGraphicsBackendId.Direct3D11)
        {
            return false;
        }

        if (NativeDeviceHandle == resource.Device.DeviceHandle)
            return true;

        // A frame from a removed decoder device (a GPU reset) is not adopted: every surface would
        // be re-created on a dead device, and a shared-texture consumer told it is unavailable
        // would stay on its frame sink (Stage 045A). Device-loss recovery (ENG-039) must not
        // adopt it again either; the reopened decoder brings a live device.
        if (D3D11DeviceResources.IsDeviceRemoved(resource.Device.DeviceHandle))
            return false;

        // A device without the video processor cannot draw its frames: refused before the
        // current device and its surfaces are torn down (ENG-039 G5). Not remembered by pointer,
        // since a later decoder device can reuse the address; the check is one QueryInterface.
        if (!D3D11DeviceResources.HasVideoSupport(resource.Device.DeviceHandle, resource.Device.DeviceContextHandle))
        {
            if (!_videoSupportRefusalReported)
            {
                _videoSupportRefusalReported = true;
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "D3D11", "The decoder's Direct3D11 device has no video processor; its hardware frames are not drawn.");
            }

            return false;
        }

        if (!InitializeFromNativeDevice(resource.Device.DeviceHandle, resource.Device.DeviceContextHandle))
            return false;

        deviceAdopted = true;
        return true;
    }

    public void ClearCompositionBackgroundFrame()
    {
        lock (_lock)
        {
            _presentation.ClearCompositionBackgroundFrame();
        }
    }

    public bool PresentComposition(MediaCompositionFrame frame)
    {
        lock (_lock)
        {
            return _presentation.PresentMediaCompositionVisible(() => _disposed, frame, RaisePresentedEvent);
        }
    }

    public string? LastPresentFailureReason
    {
        get
        {
            lock (_lock)
                return _presentation.LastPresentFailureReason;
        }
    }

    public void CleanupResources()
    {
        lock (_lock)
        {
            _presentation.CleanupResources();
        }
    }

    private void RaisePresentedEvent(int _width, int _height) => RaiseFramePresented();

    private bool NotifyPresentedResult(int _width, int _height) => NotifyPresentedCore();

    private void NotifyPresentedWithSync(int _width, int _height, bool syncImmediate) => _ = NotifyPresentedCore(syncImmediate);

    private bool NotifyPresentedCore(bool syncImmediate = false) => _presentation.NotifyFramePresented(() => _disposed, syncImmediate, RaiseFramePresented);

    private void RaiseFramePresented() => FramePresented?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_lock)
        {
            _presentation.CleanupResources();
            _disposed = true;
        }

        _refreshRates.Clear();
        _presentation.HdrOutput.Clear();
        _vblankClocks.Dispose();

        if (_configuration.EnableLogging)
        {
            Debug.WriteLine("[NativeD3D11PresentationBridge] Disposed");
        }
    }
}
