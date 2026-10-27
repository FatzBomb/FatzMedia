using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Graphics;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Core.Graphics.Rendering;

namespace FatzBomb.MediaControl.Rendering.Vulkan;

/// <summary>
/// Vulkan renderer backend, preview slices 1-2: managed BGRA frame-sink surfaces, GPU clear,
/// background blit, wash and CDG overlay draws, CPU readback into each sink. Stage 027A slice 2
/// adds the shared GPU image surface: the composed frame is blitted into one of two exported
/// images (opaque POSIX descriptors, optional exported semaphores) that the UI compositor
/// imports, with no readback. Stage 027A slice 5: any number of surfaces, of any role, each of
/// either kind; every composition is rendered once per target at the target's size.
/// Stage 045A slice 2: FFmpeg decodes on the renderer's device (Vulkan Video,
/// <see cref="IMediaRendererVideoDecodeDevice"/>), and decoded frames are converted on the GPU into
/// the composition's video layer. Window-system swapchains, subtitles and base-visual transforms
/// are later slices; frames that need them are rejected with a diagnostic instead of being drawn
/// incorrectly.
/// </summary>
internal sealed class VulkanRendererBackend(Func<VulkanOffscreenCompositor?> compositorFactory) : IMediaRendererBackend, IMediaRendererVideoDecodeDevice
{
    private readonly Lock _sync = new();
    private bool _initializationFailed;

    // Held for the whole render + sink hand-off so cleanup and dispose cannot tear the compositor
    // down mid-frame. Lock order: _renderGate, then _sync.
    private readonly Lock _renderGate = new();
    private VulkanOffscreenCompositor? _compositor;

    // Attached surfaces by id (guarded by _sync): the primary plus any secondary screens.
    private readonly Dictionary<string, Target> _targets = new(StringComparer.OrdinalIgnoreCase);
    private long _sharedGeneration;
    private VulkanFrameSupport _lastReportedUnsupported = VulkanFrameSupport.Supported;
    private MediaCompositionStretchMode _stretchMode = MediaCompositionStretchMode.UniformFit;
    private bool _foreignFrameReported;
    private bool _disposed;

    public event EventHandler? FramePresented;

    public string BackendName => "Vulkan";
    public string DeviceLostDiagnosticCategory => "Vulkan.DeviceLost";
    public string DeviceLostRecoveryFailedDiagnosticCategory => "Vulkan.DeviceLost.RecoveryFailed";
    public bool IsInitialized { get; private set; }
    public nint NativeDeviceHandle => _compositor?.DeviceHandle ?? 0;

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

    /// <summary>
    /// How long a frame waits for the compositor to give an image back before it is dropped. Kept
    /// short (half a 60 Hz frame): the wait holds the render gate that UI attach, resize and detach
    /// also take.
    /// </summary>
    internal static TimeSpan SharedImageWaitTimeout { get; } = TimeSpan.FromMilliseconds(8);

    public bool AttachSurface(MediaPresentationSurfaceDescriptor surface) => SetSurface(surface, requireExisting: false);

    public bool ResizeSurface(MediaPresentationSurfaceDescriptor surface) => SetSurface(surface, requireExisting: true);

    public bool SetSurfaceLiveResize(MediaPresentationSurfaceDescriptor surface, bool isResizing)
    {
        lock (_sync)
            return !_disposed && _targets.ContainsKey(surface.Id);
    }

    public bool DetachSurface(MediaPresentationSurfaceDescriptor surface)
    {
        lock (_renderGate)
        {
            lock (_sync)
            {
                if (_disposed || !_targets.Remove(surface.Id, out Target? target))
                    return false;

                RetireSharedNoLock(target);
                return true;
            }
        }
    }

    public void SetPresentationStretch(MediaCompositionStretchMode stretchMode)
    {
        // Composition frames carry their own stretch mode; native frames use this one.
        lock (_sync)
            _stretchMode = stretchMode;
    }

    /// <summary>
    /// Presents a decoded frame on its own (Core's path when only the primary surface is attached):
    /// composed like any other frame, the video over black, at the presentation stretch.
    /// </summary>
    public bool PresentNativeFrame(VideoFrameEventArgs frame)
    {
        if (frame.NativeResource is not { IsValid: true } resource)
            return false;

        MediaCompositionStretchMode stretch;
        lock (_sync)
            stretch = _stretchMode;

        return PresentComposition(new MediaCompositionFrame
        {
            ClearMode = MediaCompositionClearMode.Black,
            ClearArgb = 0xFF000000,
            BackgroundNativeResource = resource,
            BackgroundNativeWidth = frame.Width,
            BackgroundNativeHeight = frame.Height,
            BackgroundNativeFramesPerSecond = frame.FramesPerSecond,
            BackgroundWidth = frame.Width,
            BackgroundHeight = frame.Height,
            BackgroundStride = frame.Width * 4,
            BackgroundStretchMode = stretch,
            // Each call is a new frame.
            BackgroundRevision = -1,
            BaseVisualTransform = BaseVisualTransform.Identity,
            Reason = "native video frame",
        });
    }

    public void ClearCompositionBackgroundFrame()
    {
        VulkanOffscreenCompositor? compositor;
        lock (_sync)
            compositor = _compositor;
        compositor?.ClearVideoLayer();
    }

    public bool PresentComposition(MediaCompositionFrame frame)
    {
        VulkanOffscreenCompositor? compositor;
        Target[] targets;
        lock (_sync)
        {
            if (_disposed || _targets.Count == 0 || !Initialize())
                return false;

            compositor = _compositor;
            targets = [.. _targets.Values];
        }

        VulkanFrameSupport support = VulkanCompositionPlanner.Classify(frame);
        if (support != VulkanFrameSupport.Supported)
        {
            ReportUnsupported(support);
            return false;
        }

        // As Direct3D11 and the software renderer do: with a primary attached, success is the
        // primary's present, so a secondary that presented never hides a failed primary frame.
        var primaryExpected = false;
        var primaryPresented = false;
        var anyPresented = false;
        var anyIncluded = false;
        foreach (Target target in targets)
        {
            var isPrimary = target.Surface.Role == MediaPresentationSurfaceRole.Primary;
            // A composition for some targets only (display-locked video on several monitors).
            if (!frame.IsForTarget(isPrimary, target.Surface.Id))
                continue;

            anyIncluded = true;
            PresentResult result = target.Consumer is not null
                ? PresentShared(frame, compositor!, target)
                : PresentSink(frame, compositor!, target);
            if (result == PresentResult.DeviceLost)
            {
                CleanupResources();
                return false;
            }

            // Detached since this frame began: not a target of it any more.
            if (result == PresentResult.Detached)
                continue;

            primaryExpected |= isPrimary;
            var presented = result == PresentResult.Presented;
            primaryPresented |= isPrimary && presented;
            anyPresented |= presented;
        }

        // A filter that matches no attached target (its surface just detached) is nothing to do.
        if (!anyIncluded)
            return frame.Targets is not null;

        var succeeded = primaryExpected ? primaryPresented : anyPresented;
        if (succeeded)
        {
            _renderFailureLatch.Reset();
            FramePresented?.Invoke(this, EventArgs.Empty);
        }
        return succeeded;
    }

    private readonly RendererDiagnosticLatch _renderFailureLatch = new();

    private enum PresentResult
    {
        Presented,
        Failed,
        Detached,
        DeviceLost,
    }

    /// <summary>Renders at the target's size and reads the frame back into its sink.</summary>
    private PresentResult PresentSink(MediaCompositionFrame frame, VulkanOffscreenCompositor compositor, Target target)
    {
        lock (_renderGate)
        {
            IMediaPresentationFrameSink? sink;
            int width;
            int height;
            lock (_sync)
            {
                if (!IsCurrentNoLock(target) || target.Sink is null)
                    return PresentResult.Detached;
                if (_disposed || !ReferenceEquals(_compositor, compositor))
                    return PresentResult.Failed;

                sink = target.Sink;
                width = target.Surface.PixelWidth;
                height = target.Surface.PixelHeight;
            }

            try
            {
                var pixels = compositor.Render(VulkanCompositionPlanner.Plan(frame, width, height));
                sink.PresentFrame(pixels, width, height, width * 4);
                return PresentResult.Presented;
            }
            catch (VulkanException exception)
            {
                return ReportRenderFailure(exception, compositor);
            }
        }
    }

    private PresentResult ReportRenderFailure(VulkanException exception, VulkanOffscreenCompositor compositor)
    {
        // Device loss is always reported: it drives recovery. Other failures repeat per frame, so
        // they are reported once until a frame presents again (ENG-026).
        if (exception.IsDeviceLost)
        {
            // A decoder on the same device finds it lost too (Stage 045A slice 2).
            compositor.MarkDeviceLost();
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, DeviceLostDiagnosticCategory, exception.Message, innerException: exception);
            return PresentResult.DeviceLost;
        }

        _ = _renderFailureLatch.Raise(MediaDiagnosticLevel.Error, "Vulkan", exception.Message, exception);
        return PresentResult.Failed;
    }

    public void CleanupResources()
    {
        lock (_renderGate)
        {
            lock (_sync)
            {
                // Surfaces stay attached: their shared images are exported again on the next frame.
                foreach (Target target in _targets.Values)
                    RetireSharedNoLock(target);
                _compositor?.Dispose();
                _compositor = null;
                IsInitialized = false;
                _initializationFailed = false;
            }
        }
    }

    /// <summary>
    /// Decoded frames are drawn only when FFmpeg decoded them on this renderer's device; nothing is
    /// adopted (the decoder uses the renderer's device, Stage 045A slice 2). A frame from another
    /// device (one replaced after a device loss, until the decoder recovers) is skipped.
    /// </summary>
    public bool EnsureNativeFrameDevice(VideoFrameEventArgs frame, out bool deviceAdopted)
    {
        deviceAdopted = false;
        lock (_sync)
        {
            if (_disposed || !Initialize() || frame.NativeResource is not { IsValid: true } resource)
                return false;

            if (_compositor!.IsOnThisDevice(resource))
            {
                _foreignFrameReported = false;
                return true;
            }

            if (_foreignFrameReported)
                return false;
            _foreignFrameReported = true;
        }

        DiagnosticRouter.Raise(
            MediaDiagnosticLevel.Warning,
            "Vulkan",
            $"A hardware video frame from another device ({frame.NativeResource.BackendId}) cannot be drawn; it is skipped until the decoder moves to this renderer's device.");
        return false;
    }

    // Stage 045A slice 2: FFmpeg decodes on the renderer's Vulkan device.
    public string? VideoDecodeDeviceType
    {
        get
        {
            lock (_sync)
                return !_disposed && Initialize() && _compositor!.VideoDecode is not null ? VideoDecodeDeviceTypeName : null;
        }
    }

    public string? VideoDecodeUnavailableReason
    {
        get
        {
            lock (_sync)
            {
                if (_disposed || !Initialize())
                    return "no usable Vulkan device";
                return _compositor!.VideoDecode is null ? _compositor.VideoDecodeUnavailableReason ?? "the Vulkan device does not decode video" : null;
            }
        }
    }

    /// <summary>The FFmpeg hardware device type the renderer decodes with.</summary>
    internal const string VideoDecodeDeviceTypeName = "vulkan";

    public nint CreateVideoDecodeDeviceContext(out string? failureReason)
    {
        lock (_sync)
        {
            if (_disposed || !Initialize())
            {
                failureReason = "no usable Vulkan device";
                return 0;
            }

            return _compositor!.CreateVideoDecodeDeviceContext(out failureReason);
        }
    }

    public NativeVideoDeviceResource? CreateVideoDecodeDeviceResource(nint ffmpegHardwareDeviceContext) =>
        VulkanOffscreenCompositor.CreateDeviceResource(ffmpegHardwareDeviceContext);

    public NativeVideoFrameResource? CreateVideoDecodeFrameResource(NativeVideoDeviceResource device, nint ffmpegFrame) =>
        VulkanOffscreenCompositor.CreateFrameResource(device, ffmpegFrame);

    public bool IsVideoDecodeDeviceLost(NativeVideoDeviceResource device) =>
        VulkanSharedDevice.Find(device.DeviceHandle)?.ProbeLost() ?? true;

    public void Dispose()
    {
        lock (_renderGate)
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;
                foreach (Target target in _targets.Values)
                    RetireSharedNoLock(target);
                _targets.Clear();
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
            $"Vulkan preview slice cannot draw this frame yet ({support}); only clear, software BGRA and Vulkan-decoded backgrounds, wash and CDG overlays are supported.");
    }

    private bool IsCurrentNoLock(Target target) =>
        _targets.TryGetValue(target.Surface.Id, out Target? current) && ReferenceEquals(current, target);

    private bool SetSurface(MediaPresentationSurfaceDescriptor surface, bool requireExisting)
    {
        if (MediaSharedGpuImageConsumerHandle.TryResolve(surface, out IMediaSharedGpuImageConsumer? consumer))
            return SetSharedSurface(surface, consumer!, requireExisting);

        if (!TryGetManagedFrameSink(surface, out IMediaPresentationFrameSink? sink))
            return false;

        lock (_renderGate)
        {
            lock (_sync)
            {
                if (_disposed || (requireExisting && !_targets.ContainsKey(surface.Id)))
                    return false;

                Target target = GetOrAddTargetNoLock(surface);
                RetireSharedNoLock(target);
                target.Consumer = null;
                target.Sink = sink;
                return true;
            }
        }
    }

    private Target GetOrAddTargetNoLock(MediaPresentationSurfaceDescriptor surface)
    {
        if (_targets.TryGetValue(surface.Id, out Target? target))
        {
            target.Surface = surface;
            return target;
        }

        target = new Target(surface);
        _targets.Add(surface.Id, target);
        return target;
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
                var existed = _targets.TryGetValue(surface.Id, out Target? target);
                if (_disposed || (requireExisting && !existed))
                    return false;

                // A resize keeps the target object, so a frame already under way still finds it.
                target ??= new Target(surface);
                target.Surface = surface;
                target.Sink = null;
                target.Consumer = consumer;
                if (!TryCreateSharedNoLock(target, surface.PixelWidth, surface.PixelHeight, out var reason))
                {
                    // The old images are retired; the surface is not served any more.
                    if (existed)
                        _ = _targets.Remove(surface.Id);
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "Vulkan", $"Shared GPU images unavailable for '{surface.Id}': {reason}");
                    consumer.OnUnavailable(reason!);
                    return false;
                }

                if (!existed)
                    _targets.Add(surface.Id, target);
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

    private bool TryCreateSharedNoLock(Target target, int width, int height, out string? reason)
    {
        RetireSharedNoLock(target);
        IMediaSharedGpuImageConsumer consumer = target.Consumer!;
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
                // A render-complete signal nobody waited on would make the next signal invalid:
                // start a fresh set instead. Releases of a retired set change nothing.
                if (ring.Release(frame, consumed) && !consumed && semaphoreSync)
                    Volatile.Write(ref target.RecreateShared, 1);
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
        target.Shared = new SharedTarget(exported, set, ring);
        Volatile.Write(ref target.RecreateShared, 0);
        target.DropReported = false;
        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "Vulkan", $"Shared GPU images exported for '{target.Surface.Id}': {set}");
        consumer.OnImagesCreated(set);
        reason = null;
        return true;
    }

    /// <summary>Renders into the target's next free exported image and announces it; drops the frame when the compositor holds both.</summary>
    private PresentResult PresentShared(MediaCompositionFrame frame, VulkanOffscreenCompositor compositor, Target target)
    {
        lock (_renderGate)
        {
            SharedTarget? shared;
            IMediaSharedGpuImageConsumer? consumer;
            int width;
            int height;
            lock (_sync)
            {
                if (!IsCurrentNoLock(target) || target.Consumer is null)
                    return PresentResult.Detached;
                if (_disposed || !ReferenceEquals(_compositor, compositor))
                    return PresentResult.Failed;

                // Read under the lock: a resize may have landed since the frame was scheduled.
                width = target.Surface.PixelWidth;
                height = target.Surface.PixelHeight;

                if (Volatile.Read(ref target.RecreateShared) != 0 || target.Shared is null || target.Shared.Images.Width != width || target.Shared.Images.Height != height)
                {
                    if (!TryCreateSharedNoLock(target, width, height, out var reason))
                    {
                        target.Consumer.OnUnavailable(reason!);
                        return PresentResult.Failed;
                    }
                }

                shared = target.Shared!;
                consumer = target.Consumer;
            }

            if (!shared.Ring.TryBeginWrite(SharedImageWaitTimeout, out MediaSharedGpuImageWrite write))
            {
                // The compositor still shows both images: it is behind (or not rendering), and a
                // newer frame follows. Not a failure.
                if (!target.DropReported)
                {
                    target.DropReported = true;
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "Vulkan", $"Shared GPU image frame dropped for '{target.Surface.Id}': the compositor has not released an image yet.");
                }

                return PresentResult.Presented;
            }

            try
            {
                compositor.RenderToExport(VulkanCompositionPlanner.Plan(frame, width, height), shared.Images, write.BufferIndex, write.WaitForConsumerRelease);
                consumer.OnFrameReady(shared.Ring.CompleteWrite(write.BufferIndex));
                return PresentResult.Presented;
            }
            catch (VulkanException exception)
            {
                shared.Ring.AbortWrite(write.BufferIndex);
                return ReportRenderFailure(exception, compositor);
            }
        }
    }

    private void RetireSharedNoLock(Target target)
    {
        SharedTarget? shared = target.Shared;
        target.Shared = null;
        if (shared is null)
            return;

        shared.Set.Retire();
        _compositor?.DestroyExportedImages(shared.Images, closeDescriptors: false);
        shared.Ring.Dispose();
    }

    /// <summary>
    /// One attached surface: a frame sink, or a shared GPU image consumer with its exported set.
    /// Mutable fields are guarded by the backend's locks (the release callback only sets
    /// <see cref="RecreateShared"/>).
    /// </summary>
    private sealed class Target(MediaPresentationSurfaceDescriptor surface)
    {
        public int RecreateShared;

        public MediaPresentationSurfaceDescriptor Surface { get; set; } = surface;
        public IMediaPresentationFrameSink? Sink { get; set; }
        public IMediaSharedGpuImageConsumer? Consumer { get; set; }
        public SharedTarget? Shared { get; set; }
        public bool DropReported { get; set; }
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
        return surface is { IsValid: true }
            && surface.NativeSurface.SurfaceTypeId == MediaNativeSurfaceTypeIds.ManagedBgraFrameSink
            && surface.NativeSurface.TryGetHandle(MediaNativeHandleIds.FrameSink, out nint handle)
            && MediaPresentationFrameSinkHandle.TryResolve(handle, out sink)
            && sink != null;
    }
}
