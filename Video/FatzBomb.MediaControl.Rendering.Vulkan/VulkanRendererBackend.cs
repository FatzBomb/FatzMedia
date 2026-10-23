using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Graphics.Composition;

namespace FatzBomb.MediaControl.Rendering.Vulkan;

/// <summary>
/// Vulkan renderer backend, preview slices 1-2: one primary managed BGRA frame-sink surface,
/// GPU clear, background blit, wash and CDG overlay draws, CPU readback into the sink. Stage 027A
/// slice 2 adds the shared GPU image surface: the composed frame is blitted into one of two
/// exported images (opaque POSIX descriptors, optional exported semaphores) that the UI
/// compositor imports, with no readback.
/// Window-system swapchains, subtitles, base-visual transforms, native video resources, and
/// multi-target fan-out are later slices; frames that need them are rejected with a diagnostic
/// instead of being drawn incorrectly.
/// </summary>
internal sealed class VulkanRendererBackend(Func<VulkanOffscreenCompositor?> compositorFactory) : IMediaRendererBackend
{
    private readonly Lock _sync = new();
    private bool _initializationFailed;

    // Held for the whole render + sink hand-off so cleanup and dispose cannot tear the compositor
    // down mid-frame. Lock order: _renderGate, then _sync.
    private readonly Lock _renderGate = new();
    private VulkanOffscreenCompositor? _compositor;
    private MediaPresentationSurfaceDescriptor? _surface;
    private IMediaPresentationFrameSink? _sink;
    private IMediaSharedGpuImageConsumer? _consumer;
    private SharedTarget? _shared;
    private long _sharedGeneration;
    private int _recreateShared;
    private bool _dropReported;
    private VulkanFrameSupport _lastReportedUnsupported = VulkanFrameSupport.Supported;
    private bool _disposed;

    public event EventHandler? FramePresented;

    public string BackendName => "Vulkan";
    public string DeviceLostDiagnosticCategory => "Vulkan.DeviceLost";
    public string DeviceLostRecoveryFailedDiagnosticCategory => "Vulkan.DeviceLost.RecoveryFailed";
    public bool IsInitialized { get; private set; }
    public nint NativeDeviceHandle => 0;

    /// <summary>Gets the selected Vulkan device name, once initialized.</summary>
    public string? DeviceName => _compositor?.DeviceName;

    public bool Initialize()
    {
        lock (_sync)
        {
            if (_disposed)
                return false;
            if (_compositor is not null)
                return IsInitialized = true;

            // Device creation is expensive and reports its failure; without a usable device it
            // would otherwise be retried (and reported) on every frame. CleanupResources allows
            // a new attempt.
            if (_initializationFailed)
                return false;

            _compositor = compositorFactory();
            IsInitialized = _compositor is not null;
            _initializationFailed = !IsInitialized;
            return IsInitialized;
        }
    }

    /// <summary>How long a frame waits for the compositor to give an image back before it is dropped.</summary>
    internal static TimeSpan SharedImageWaitTimeout { get; } = TimeSpan.FromMilliseconds(50);

    public bool AttachSurface(MediaPresentationSurfaceDescriptor surface) => SetSurface(surface, requireExisting: false);

    public bool ResizeSurface(MediaPresentationSurfaceDescriptor surface) => SetSurface(surface, requireExisting: true);

    public bool SetSurfaceLiveResize(MediaPresentationSurfaceDescriptor surface, bool isResizing)
    {
        lock (_sync)
            return !_disposed && _surface?.Id == surface.Id;
    }

    public bool DetachSurface(MediaPresentationSurfaceDescriptor surface)
    {
        lock (_renderGate)
        {
            lock (_sync)
            {
                if (_disposed || _surface?.Id != surface.Id)
                    return false;

                _surface = null;
                _sink = null;
                _consumer = null;
                RetireSharedNoLock();
                return true;
            }
        }
    }

    public void SetPresentationStretch(MediaCompositionStretchMode stretchMode)
    {
        // Each composition frame carries its effective stretch mode.
    }

    public bool PresentNativeFrame(VideoFrameEventArgs frame) => false;

    public void ClearCompositionBackgroundFrame()
    {
        // No retained background resource exists in this slice.
    }

    public bool PresentComposition(MediaCompositionFrame frame)
    {
        VulkanOffscreenCompositor? compositor;
        IMediaPresentationFrameSink? sink;
        int width;
        int height;
        lock (_sync)
        {
            if (_disposed || _surface is null || (_sink is null && _consumer is null) || !Initialize())
                return false;

            compositor = _compositor;
            sink = _sink;
            width = _surface.PixelWidth;
            height = _surface.PixelHeight;
        }

        VulkanFrameSupport support = VulkanCompositionPlanner.Classify(frame);
        if (support != VulkanFrameSupport.Supported)
        {
            ReportUnsupported(support);
            return false;
        }

        if (sink is null)
            return PresentShared(frame, compositor!, width, height);

        var deviceLost = false;
        lock (_renderGate)
        {
            lock (_sync)
            {
                if (_disposed || !ReferenceEquals(_compositor, compositor))
                    return false;
            }

            try
            {
                var pixels = compositor!.Render(VulkanCompositionPlanner.Plan(frame, width, height));
                sink!.PresentFrame(pixels, width, height, width * 4);
            }
            catch (VulkanException exception)
            {
                DiagnosticRouter.Raise(
                    MediaDiagnosticLevel.Error,
                    exception.IsDeviceLost ? DeviceLostDiagnosticCategory : "Vulkan",
                    exception.Message,
                    innerException: exception);
                if (!exception.IsDeviceLost)
                    return false;

                deviceLost = true;
            }
        }

        if (deviceLost)
        {
            CleanupResources();
            return false;
        }

        FramePresented?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void CleanupResources()
    {
        lock (_renderGate)
        {
            lock (_sync)
            {
                RetireSharedNoLock();
                _compositor?.Dispose();
                _compositor = null;
                IsInitialized = false;
                _initializationFailed = false;
            }
        }
    }

    public bool EnsureNativeFrameDevice(VideoFrameEventArgs frame, out bool deviceAdopted)
    {
        deviceAdopted = false;
        return false;
    }

    public void Dispose()
    {
        lock (_renderGate)
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _surface = null;
                _sink = null;
                _consumer = null;
                RetireSharedNoLock();
                _compositor?.Dispose();
                _compositor = null;
                IsInitialized = false;
            }
        }
    }

    private void ReportUnsupported(VulkanFrameSupport support)
    {
        lock (_sync)
        {
            if (_lastReportedUnsupported == support)
                return;
            _lastReportedUnsupported = support;
        }

        DiagnosticRouter.Raise(
            MediaDiagnosticLevel.Warning,
            "Vulkan",
            $"Vulkan preview slice cannot draw this frame yet ({support}); only clear, software BGRA backgrounds, wash and CDG overlays are supported.");
    }

    private bool SetSurface(MediaPresentationSurfaceDescriptor surface, bool requireExisting)
    {
        if (surface.Role == MediaPresentationSurfaceRole.Primary
            && MediaSharedGpuImageConsumerHandle.TryResolve(surface, out IMediaSharedGpuImageConsumer? consumer))
        {
            return SetSharedSurface(surface, consumer!, requireExisting);
        }

        if (!TryGetManagedFrameSink(surface, out IMediaPresentationFrameSink? sink))
            return false;

        lock (_renderGate)
        {
            lock (_sync)
            {
                if (_disposed || (requireExisting && _surface?.Id != surface.Id))
                    return false;

                RetireSharedNoLock();
                _consumer = null;
                _surface = surface;
                _sink = sink;
                return true;
            }
        }
    }

    /// <summary>
    /// Attaches (or resizes) a shared GPU image surface: negotiates with the consumer's request,
    /// exports a new double-buffered image set at the surface size, and hands it over. Declines,
    /// with the reason told to the consumer, when this device cannot export what it imports.
    /// </summary>
    private bool SetSharedSurface(MediaPresentationSurfaceDescriptor surface, IMediaSharedGpuImageConsumer consumer, bool requireExisting)
    {
        // The device must exist to know what it can export; Core initializes before attaching.
        if (!Initialize())
        {
            consumer.OnUnavailable("no usable Vulkan device");
            return false;
        }

        lock (_renderGate)
        {
            lock (_sync)
            {
                if (_disposed || (requireExisting && _surface?.Id != surface.Id))
                    return false;

                if (!TryCreateSharedNoLock(consumer, surface.PixelWidth, surface.PixelHeight, out var reason))
                {
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "Vulkan", $"Shared GPU images unavailable: {reason}");
                    consumer.OnUnavailable(reason!);
                    return false;
                }

                _surface = surface;
                _sink = null;
                _consumer = consumer;
                return true;
            }
        }
    }

    /// <summary>The producer side of what this device can export (for the consumer's negotiation).</summary>
    internal static (MediaSharedGpuImageCapability[] Images, MediaSharedGpuSemaphoreHandleKind[] Semaphores) GetExportCapabilities(
        bool canExportMemoryFd,
        bool canExportSemaphoreFd)
    {
        if (!canExportMemoryFd)
            return ([], []);

        // Every frame completes on the GPU before it is announced, so host synchronization always
        // works; exported semaphores additionally let the compositor wait on the GPU.
        MediaSharedGpuImageSyncKinds sync = MediaSharedGpuImageSyncKinds.HostSynchronized
            | (canExportSemaphoreFd ? MediaSharedGpuImageSyncKinds.Semaphores : MediaSharedGpuImageSyncKinds.None);
        return (
            [new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.VulkanOpaqueFd, sync)],
            canExportSemaphoreFd ? [MediaSharedGpuSemaphoreHandleKind.VulkanOpaqueFd] : []);
    }

    private bool TryCreateSharedNoLock(IMediaSharedGpuImageConsumer consumer, int width, int height, out string? reason)
    {
        RetireSharedNoLock();
        if (_compositor is not { } compositor)
        {
            reason = "no usable Vulkan device";
            return false;
        }

        if (!compositor.CanExportMemoryFd)
        {
            reason = compositor.ExportUnavailableReason ?? "Vulkan memory export is unavailable";
            return false;
        }

        MediaSharedGpuImageRequest request = consumer.Request;
        (MediaSharedGpuImageCapability[] images, MediaSharedGpuSemaphoreHandleKind[] semaphores) =
            GetExportCapabilities(compositor.CanExportMemoryFd, compositor.CanExportSemaphoreFd);
        if (!request.TrySelect(images, semaphores, out MediaSharedGpuImageSelection selection, out reason)
            || !request.MatchesDevice(compositor.DeviceLuid, compositor.DeviceUuid, out reason))
        {
            return false;
        }

        VulkanExportedImageSet exported;
        try
        {
            exported = compositor.CreateExportedImages(width, height, SharedTarget.BufferCount, selection.SyncKind == MediaSharedGpuImageSyncKinds.Semaphores);
        }
        catch (Exception exception) when (exception is VulkanException or InvalidOperationException)
        {
            reason = $"exporting the shared images failed: {exception.Message}";
            return false;
        }

        var generation = ++_sharedGeneration;
        var ring = new MediaSharedGpuImageRing(SharedTarget.BufferCount);
        ring.Reset(generation);
        var semaphoreSync = selection.SyncKind == MediaSharedGpuImageSyncKinds.Semaphores;
        var set = new MediaSharedGpuImageSet(
            generation,
            selection,
            width,
            height,
            MediaSharedGpuImageFormat.Rgba8Unorm,
            exported.Images.Select(static image => new MediaSharedGpuImageBuffer(
                image.MemoryFd,
                image.MemorySize,
                0,
                image.RenderCompleteFd,
                image.ConsumerReleaseFd)),
            (frame, consumed) =>
            {
                _ = ring.Release(frame, consumed);
                // A render-complete signal nobody waited on would make the next signal invalid:
                // start a fresh set instead.
                if (!consumed && semaphoreSync)
                    Volatile.Write(ref _recreateShared, 1);
            },
            consumed =>
            {
                if (consumed)
                    return;

                foreach (VulkanExportedImage image in exported.Images)
                {
                    VulkanOffscreenCompositor.CloseDescriptor(image.MemoryFd);
                    VulkanOffscreenCompositor.CloseDescriptor(image.RenderCompleteFd);
                    VulkanOffscreenCompositor.CloseDescriptor(image.ConsumerReleaseFd);
                }
            },
            (int)VulkanOffscreenCompositor.ExportLayout);
        _shared = new SharedTarget(exported, set, ring);
        Volatile.Write(ref _recreateShared, 0);
        _dropReported = false;
        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "Vulkan", $"Shared GPU images exported: {set}");
        consumer.OnImagesCreated(set);
        reason = null;
        return true;
    }

    /// <summary>Renders into the next free exported image and announces it; drops the frame when the compositor holds both.</summary>
    private bool PresentShared(MediaCompositionFrame frame, VulkanOffscreenCompositor compositor, int width, int height)
    {
        var deviceLost = false;
        lock (_renderGate)
        {
            SharedTarget? shared;
            IMediaSharedGpuImageConsumer? consumer;
            lock (_sync)
            {
                if (_disposed || !ReferenceEquals(_compositor, compositor) || _consumer is null)
                    return false;

                if (Volatile.Read(ref _recreateShared) != 0 || _shared is null || _shared.Images.Width != width || _shared.Images.Height != height)
                {
                    if (!TryCreateSharedNoLock(_consumer, width, height, out var reason))
                    {
                        _consumer.OnUnavailable(reason!);
                        return false;
                    }
                }

                shared = _shared!;
                consumer = _consumer;
            }

            if (!shared.Ring.TryBeginWrite(SharedImageWaitTimeout, out MediaSharedGpuImageWrite write))
            {
                // The compositor still shows both images: it is behind (or not rendering), and a
                // newer frame follows. Not a failure.
                if (!_dropReported)
                {
                    _dropReported = true;
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "Vulkan", "Shared GPU image frame dropped: the compositor has not released an image yet.");
                }

                return true;
            }

            try
            {
                compositor.RenderToExport(VulkanCompositionPlanner.Plan(frame, width, height), shared.Images, write.BufferIndex, write.WaitForConsumerRelease);
                consumer.OnFrameReady(shared.Ring.CompleteWrite(write.BufferIndex));
            }
            catch (VulkanException exception)
            {
                shared.Ring.AbortWrite(write.BufferIndex);
                DiagnosticRouter.Raise(
                    MediaDiagnosticLevel.Error,
                    exception.IsDeviceLost ? DeviceLostDiagnosticCategory : "Vulkan",
                    exception.Message,
                    innerException: exception);
                if (!exception.IsDeviceLost)
                    return false;

                deviceLost = true;
            }
        }

        if (deviceLost)
        {
            CleanupResources();
            return false;
        }

        FramePresented?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void RetireSharedNoLock()
    {
        SharedTarget? shared = _shared;
        _shared = null;
        if (shared is null)
            return;

        shared.Set.Retire();
        _compositor?.DestroyExportedImages(shared.Images, closeDescriptors: false);
        shared.Ring.Dispose();
    }

    private sealed class SharedTarget(VulkanExportedImageSet images, MediaSharedGpuImageSet set, MediaSharedGpuImageRing ring)
    {
        public const int BufferCount = 2;

        public VulkanExportedImageSet Images { get; } = images;
        public MediaSharedGpuImageSet Set { get; } = set;
        public MediaSharedGpuImageRing Ring { get; } = ring;
    }

    private static bool TryGetManagedFrameSink(MediaPresentationSurfaceDescriptor? surface, out IMediaPresentationFrameSink? sink)
    {
        sink = null;
        return surface is { IsValid: true, Role: MediaPresentationSurfaceRole.Primary }
            && surface.NativeSurface.SurfaceTypeId == MediaNativeSurfaceTypeIds.ManagedBgraFrameSink
            && surface.NativeSurface.TryGetHandle(MediaNativeHandleIds.FrameSink, out nint handle)
            && MediaPresentationFrameSinkHandle.TryResolve(handle, out sink)
            && sink != null;
    }
}
