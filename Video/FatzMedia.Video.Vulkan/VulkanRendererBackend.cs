using System.Diagnostics;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Graphics;
using FatzMedia.Video.Core.Graphics.Composition;
using FatzMedia.Video.Core.Graphics.Rendering;

namespace FatzMedia.Video.Vulkan;

/// <summary>What a Vulkan device can share with a UI compositor (Stage 027A slice 2; Windows: Stage 045A slice 3).</summary>
internal readonly record struct VulkanExportSupport(bool MemoryFd, bool SemaphoreFd, bool MemoryWin32, bool SemaphoreWin32, bool D3D11KeyedMutex);

/// <summary>
/// Vulkan renderer backend, preview slices 1-2: managed BGRA frame-sink surfaces, GPU clear,
/// background blit, wash and CDG overlay draws, CPU readback into each sink. Stage 027A slice 2
/// adds the shared GPU image surface: the composed frame is blitted into one of two exported
/// images (opaque POSIX descriptors, optional exported semaphores; on Windows, Stage 045A slice 3,
/// opaque NT handles or keyed-mutex Direct3D11 textures) that the UI compositor imports, with no
/// readback. Stage 027A slice 5: any number of surfaces, of any role, each of
/// either kind; every composition is rendered once per target at the target's size.
/// Stage 045A slice 2: FFmpeg decodes on the renderer's device (Vulkan Video,
/// <see cref="IMediaRendererVideoDecodeDevice"/>), and decoded frames are converted on the GPU into
/// the composition's video layer. Subtitle and karaoke rasters (SkiaSharp, from
/// <c>FatzMedia.Video.Text</c>) are drawn as a premultiplied overlay. Window-system
/// swapchains and base-visual transforms are later slices; frames that need them are rejected with
/// a diagnostic instead of being drawn incorrectly.
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

    // ENG-039: a lost device stays in place (refusing frames and device requests) until Core's
    // recovery calls CleanupResources, so one loss re-creates the device exactly once.
    private bool _deviceLost;
    private string? _deviceLostReason;

    // Allocation failures in a row (guarded by _renderGate): after a GPU reset NVIDIA reported
    // VK_ERROR_OUT_OF_DEVICE_MEMORY, not a lost device.
    private int _allocationFailures;

    // When a consumer last reported its graphics context lost (Stopwatch timestamp, 0 = never).
    private long _consumerLostTimestamp;

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
            // A lost device is re-created by CleanupResources first (Core's recovery), never here.
            if (_disposed || _deviceLost)
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

    /// <summary>
    /// How long the compositor may hold every image before the set is replaced (ENG-039: after a
    /// GPU reset it never gave them back, and frames were dropped silently for good).
    /// </summary>
    internal TimeSpan SharedImageStallTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Allocation failures in a row that count as a lost device (ENG-039).</summary>
    internal const int OutOfMemoryFailuresBeforeLost = 3;

    /// <summary>How long after a consumer's context loss a single allocation failure counts as a lost device.</summary>
    internal static readonly TimeSpan ConsumerLossWindow = TimeSpan.FromSeconds(5);

    /// <summary>Gets how many exports were logged at Info (tests, G6).</summary>
    internal int ExportsLogged { get; private set; }

    /// <summary>Gets whether the device is marked lost and waits for <see cref="CleanupResources"/> (tests).</summary>
    internal bool IsDeviceLostPending
    {
        get
        {
            lock (_sync)
                return _deviceLost;
        }
    }

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
        var outcome = new CompositionPresentOutcome();
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
            // Core's recovery re-creates the device (ProbeDeviceLost now reports it).
            if (result == PresentResult.DeviceLost)
                return false;

            // Detached since this frame began: not a target of it any more.
            if (result == PresentResult.Detached)
                continue;

            outcome.Record(isPrimary, result == PresentResult.Presented);
        }

        // A filter that matches no attached target (its surface just detached) is nothing to do.
        if (!anyIncluded)
            return frame.Targets is not null;

        var succeeded = outcome.Succeeded;
        if (succeeded)
        {
            _renderFailureLatch.Reset();
            FramePresented?.Invoke(this, EventArgs.Empty);
        }
        return succeeded;
    }

    private readonly RendererDiagnosticLatch _renderFailureLatch = new();

    internal enum PresentResult
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
                _allocationFailures = 0;
                return PresentResult.Presented;
            }
            catch (VulkanException exception)
            {
                return ReportRenderFailure(exception, compositor);
            }
        }
    }

    /// <summary>
    /// Classifies a failed frame (called under <see cref="_renderGate"/>). Device loss is always
    /// reported: it drives recovery. ENG-039: any other failure probes the device, since after an
    /// NVIDIA GPU reset the first failure was <c>VK_ERROR_OUT_OF_DEVICE_MEMORY</c>; allocation
    /// failures count as a loss after <see cref="OutOfMemoryFailuresBeforeLost"/> in a row, or at
    /// once just after a consumer lost its graphics context. Other failures repeat per frame, so
    /// they are reported once until a frame presents again (ENG-026).
    /// </summary>
    internal PresentResult ReportRenderFailure(VulkanException exception, VulkanOffscreenCompositor compositor)
    {
        if (exception.IsDeviceLost)
        {
            EnterDeviceLost(compositor, exception.Message, raise: true, exception);
            return PresentResult.DeviceLost;
        }

        if (compositor.ProbeDeviceLost(out var probed))
        {
            EnterDeviceLost(compositor, $"{exception.Message}; {probed}", raise: true, exception);
            return PresentResult.DeviceLost;
        }

        if (exception.IsOutOfMemory)
        {
            var failures = ++_allocationFailures;
            var consumerLost = Volatile.Read(ref _consumerLostTimestamp) is var lostAt and not 0
                && Stopwatch.GetElapsedTime(lostAt) < ConsumerLossWindow;
            if (failures >= OutOfMemoryFailuresBeforeLost || consumerLost)
            {
                var why = consumerLost ? "just after the compositor lost its graphics context" : $"{failures} times in a row";
                EnterDeviceLost(compositor, $"{exception.Message} {why}; re-creating the device", raise: true, exception);
                return PresentResult.DeviceLost;
            }
        }

        _ = _renderFailureLatch.Raise(MediaDiagnosticLevel.Error, "Vulkan", exception.Message, exception);
        return PresentResult.Failed;
    }

    /// <summary>
    /// Marks <paramref name="compositor"/>'s device lost, once, if it is still the current one: frames
    /// and device requests are refused until <see cref="CleanupResources"/>, and a decoder on the
    /// device finds it lost. With <paramref name="raise"/> (a failed frame, on the player's thread)
    /// the loss is also raised in <see cref="DeviceLostDiagnosticCategory"/>, which Core still
    /// accepts as a recovery trigger; otherwise Core's probe reports it.
    /// </summary>
    private void EnterDeviceLost(VulkanOffscreenCompositor compositor, string reason, bool raise, Exception? exception = null)
    {
        lock (_sync)
        {
            if (_disposed || _deviceLost || !ReferenceEquals(_compositor, compositor))
                return;

            _deviceLost = true;
            _deviceLostReason = reason;
        }

        // A decoder on the same device finds it lost too (Stage 045A slice 2).
        compositor.MarkDeviceLost(reason);
        if (raise)
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, DeviceLostDiagnosticCategory, reason, innerException: exception);
    }

    /// <summary>
    /// ENG-039: whether the device is lost, marked by a failed frame or found now (the Direct3D11
    /// interop device's removal, or the heartbeat). Takes no render lock; waits at most about
    /// 350 ms. Core polls it and calls <see cref="CleanupResources"/> and <see cref="Initialize"/>.
    /// </summary>
    public bool ProbeDeviceLost(out string? reason)
    {
        VulkanOffscreenCompositor? compositor;
        lock (_sync)
        {
            reason = _deviceLost ? _deviceLostReason : null;
            if (_disposed || _deviceLost)
                return _deviceLost && !_disposed;

            compositor = _compositor;
        }

        if (compositor is null || !compositor.ProbeDeviceLost(out reason))
        {
            reason = null;
            return false;
        }

        reason ??= "the Vulkan device is lost";
        EnterDeviceLost(compositor, reason, raise: false);
        return true;
    }

    /// <summary>
    /// A consumer's compositor lost its graphics context (a GPU reset): note it for the
    /// allocation-failure rule and probe this device off the caller's thread (ENG-039).
    /// </summary>
    private void OnConsumerDeviceLost(string consumerReason)
    {
        Volatile.Write(ref _consumerLostTimestamp, Stopwatch.GetTimestamp());
        VulkanOffscreenCompositor? compositor;
        lock (_sync)
            compositor = _disposed || _deviceLost ? null : _compositor;
        if (compositor is null)
            return;

        _ = Task.Run(() =>
        {
            if (compositor.ProbeDeviceLost(out var reason))
                EnterDeviceLost(compositor, $"the compositor lost its graphics context ({consumerReason}); {reason}", raise: false);
        });
    }

    public void CleanupResources()
    {
        VulkanOffscreenCompositor? lostCompositor = null;
        var start = Stopwatch.GetTimestamp();
        lock (_renderGate)
        {
            lock (_sync)
            {
                VulkanStallMeter.Report(start, "Vulkan clean-up lock wait");
                // ENG-050: a lost device's objects are destroyed off the recovery path. After an
                // NVIDIA GPU reset, calls on the old device (and a heartbeat probe holding the
                // compositor's probe gate) blocked until the driver finished the reset, about 15 s,
                // before the new device was even tried. Nothing else uses the old compositor once it
                // is detached here; its Dispose destroys the retired images with it.
                var deviceLost = _deviceLost || _compositor?.SharedDevice?.IsLost == true;
                // Surfaces stay attached: their shared images are exported again on the next frame.
                foreach (Target target in _targets.Values)
                    RetireSharedNoLock(target, destroyImages: !deviceLost);
                if (deviceLost)
                    lostCompositor = _compositor;
                else
                    _compositor?.Dispose();
                _compositor = null;
                IsInitialized = false;
                _initializationFailed = false;
                _deviceLost = false;
                _deviceLostReason = null;
                _allocationFailures = 0;
            }
        }

        if (lostCompositor is not null)
            DisposeLostCompositor(lostCompositor);
    }

    /// <summary>Disposes a lost device's compositor on its own thread, timing it (ENG-050).</summary>
    private static void DisposeLostCompositor(VulkanOffscreenCompositor compositor)
    {
        // The player's diagnostics owner flows with the execution context.
        _ = Task.Run(() =>
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                compositor.Dispose();
            }
            catch (Exception ex)
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Vulkan", $"Closing the lost Vulkan device failed: {ex.Message}");
            }

            VulkanStallMeter.Report(start, "Closing the lost Vulkan device (in the background)");
        });
    }

    /// <summary>
    /// Whether another recovery attempt can succeed (ENG-050): not once <c>vkCreateInstance</c>
    /// has returned <c>VK_ERROR_INCOMPATIBLE_DRIVER</c>, which after a GPU reset stays so for the
    /// rest of the process.
    /// </summary>
    public bool CanRetryDeviceRecovery => VulkanOffscreenCompositor.LastCreateInstanceResult != Silk.NET.Vulkan.Result.ErrorIncompatibleDriver;

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
    // While the device is lost, the type stays what it was, so decoders do not give up renderer
    // decoding for good; device contexts are refused until the device is re-created.
    public string? VideoDecodeDeviceType
    {
        get
        {
            lock (_sync)
                return !_disposed && (_deviceLost || Initialize()) && _compositor?.VideoDecode is not null ? VideoDecodeDeviceTypeName : null;
        }
    }

    public string? VideoDecodeUnavailableReason
    {
        get
        {
            lock (_sync)
            {
                if (_disposed || _compositor is null || (!_deviceLost && !Initialize()))
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
            if (_deviceLost)
            {
                failureReason = "the renderer's Vulkan device was lost and is being re-created";
                return 0;
            }

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

    // Called about once a second from the decode thread (ENG-039): the lost mark and a fence
    // status only, never the heartbeat. A device already released is lost.
    public bool IsVideoDecodeDeviceLost(NativeVideoDeviceResource device)
    {
        if (VulkanSharedDevice.Find(device.DeviceHandle) is not { } shared || !shared.TryAddRef())
            return true;

        try
        {
            return shared.ProbeLost();
        }
        finally
        {
            shared.Release();
        }
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
            $"Vulkan preview slice cannot draw this frame yet ({support}); only clear, software BGRA and Vulkan-decoded backgrounds, wash, CDG overlays and subtitles are supported.");
    }

    private bool IsCurrentNoLock(Target target) =>
        _targets.TryGetValue(target.Surface.Id, out Target? current) && ReferenceEquals(current, target);

    private bool SetSurface(MediaPresentationSurfaceDescriptor surface, bool requireExisting)
    {
        if (MediaSharedGpuImageConsumerHandle.TryResolve(surface, out IMediaSharedGpuImageConsumer? consumer))
            return SetSharedSurface(surface, consumer!, requireExisting);

        if (!MediaPresentationFrameSinkHandle.TryResolve(surface, out IMediaPresentationFrameSink? sink))
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

    /// <summary>
    /// The producer side of every kind this device can share (Stage 045A slice 3 adds Windows):
    /// opaque descriptors or NT handles, with host synchronization (every frame completes on the GPU
    /// before it is announced) and, when semaphores export, GPU waits; and keyed-mutex Direct3D11
    /// textures, which Avalonia's default compositor on Windows imports.
    /// </summary>
    internal static (MediaSharedGpuImageCapability[] Images, MediaSharedGpuSemaphoreHandleKind[] Semaphores) GetExportCapabilities(VulkanExportSupport support)
    {
        List<MediaSharedGpuImageCapability> images = [];
        List<MediaSharedGpuSemaphoreHandleKind> semaphores = [];
        if (support.D3D11KeyedMutex)
            images.Add(new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, MediaSharedGpuImageSyncKinds.KeyedMutex));

        if (support.MemoryWin32)
        {
            images.Add(new MediaSharedGpuImageCapability(
                MediaSharedGpuImageHandleKind.VulkanOpaqueNtHandle,
                MediaSharedGpuImageSyncKinds.HostSynchronized | (support.SemaphoreWin32 ? MediaSharedGpuImageSyncKinds.Semaphores : MediaSharedGpuImageSyncKinds.None)));
            if (support.SemaphoreWin32)
                semaphores.Add(MediaSharedGpuSemaphoreHandleKind.VulkanOpaqueNtHandle);
        }

        if (support.MemoryFd)
        {
            images.Add(new MediaSharedGpuImageCapability(
                MediaSharedGpuImageHandleKind.VulkanOpaqueFd,
                MediaSharedGpuImageSyncKinds.HostSynchronized | (support.SemaphoreFd ? MediaSharedGpuImageSyncKinds.Semaphores : MediaSharedGpuImageSyncKinds.None)));
            if (support.SemaphoreFd)
                semaphores.Add(MediaSharedGpuSemaphoreHandleKind.VulkanOpaqueFd);
        }

        return ([.. images], [.. semaphores]);
    }

    /// <summary>The export buffer of one image, with the handles its kind uses.</summary>
    internal static MediaSharedGpuImageBuffer ToBuffer(VulkanExportedImage image, MediaSharedGpuImageHandleKind kind) => kind == MediaSharedGpuImageHandleKind.VulkanOpaqueFd
        ? new MediaSharedGpuImageBuffer(image.MemoryFd, image.MemorySize, 0, image.RenderCompleteFd, image.ConsumerReleaseFd)
        : new MediaSharedGpuImageBuffer(image.MemoryNtHandle, image.MemorySize, 0, image.RenderCompleteNtHandle, image.ConsumerReleaseNtHandle);

    private bool TryCreateSharedNoLock(Target target, int width, int height, out string? reason)
    {
        RetireSharedNoLock(target);
        IMediaSharedGpuImageConsumer consumer = target.Consumer!;
        if (_compositor is not { } compositor)
        {
            reason = "no usable Vulkan device";
            return false;
        }

        if (!compositor.CanExport)
        {
            reason = compositor.ExportUnavailableReason ?? "Vulkan memory export is unavailable";
            return false;
        }

        MediaSharedGpuImageRequest request = consumer.Request;
        (MediaSharedGpuImageCapability[] images, MediaSharedGpuSemaphoreHandleKind[] semaphores) = GetExportCapabilities(new VulkanExportSupport(
            compositor.CanExportMemoryFd,
            compositor.CanExportSemaphoreFd,
            compositor.CanExportMemoryWin32,
            compositor.CanExportSemaphoreWin32,
            compositor.CanShareD3D11Textures));
        if (!request.TrySelect(images, semaphores, out MediaSharedGpuImageSelection selection, out reason)
            || !request.MatchesDevice(compositor.DeviceLuid, compositor.DeviceUuid, out reason))
        {
            return false;
        }

        VulkanExportedImageSet exported;
        try
        {
            exported = compositor.CreateExportedImages(width, height, SharedTarget.BufferCount, selection.SyncKind == MediaSharedGpuImageSyncKinds.Semaphores, selection.HandleKind);
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
            exported.Images.Select(image => ToBuffer(image, exported.Kind)),
            (frame, consumed) =>
            {
                // A render-complete signal nobody waited on would make the next signal invalid:
                // start a fresh set instead. Releases of a retired set change nothing.
                if (!ring.Release(frame, consumed))
                    return;

                Volatile.Write(ref target.ReleasedSinceReplacement, 1);
                if (!consumed && semaphoreSync)
                    Volatile.Write(ref target.RecreateShared, 1);
            },
            consumedBuffers =>
            {
                for (var index = 0; index < exported.Images.Count; index++)
                {
                    VulkanExportedImage image = exported.Images[index];
                    // NT handles stay with the producer (importers open their own references);
                    // descriptors belong to the importer once it was handed them (ENG-039: only
                    // the buffers before the count, when an import failed part way).
                    VulkanOffscreenCompositor.CloseNtHandle(image.MemoryNtHandle);
                    VulkanOffscreenCompositor.CloseNtHandle(image.RenderCompleteNtHandle);
                    VulkanOffscreenCompositor.CloseNtHandle(image.ConsumerReleaseNtHandle);
                    if (index < consumedBuffers)
                        continue;

                    VulkanOffscreenCompositor.CloseDescriptor(image.MemoryFd);
                    VulkanOffscreenCompositor.CloseDescriptor(image.RenderCompleteFd);
                    VulkanOffscreenCompositor.CloseDescriptor(image.ConsumerReleaseFd);
                }
            },
            OnConsumerDeviceLost,
            // Direct3D11 textures have no Vulkan layout for the compositor (it never sees Vulkan).
            exported.Kind == MediaSharedGpuImageHandleKind.D3D11TextureNtHandle ? 0 : (int)VulkanOffscreenCompositor.ExportLayout);
        target.Shared = new SharedTarget(exported, set, ring);
        Volatile.Write(ref target.RecreateShared, 0);
        target.StarvedSince = 0;
        // G6: a resize or re-export for the same consumer and selection is not news (423 lines in
        // 15 s of resizing); a new consumer or selection is.
        if (!ReferenceEquals(target.LoggedConsumer, consumer) || target.LoggedSelection != selection)
        {
            target.LoggedConsumer = consumer;
            target.LoggedSelection = selection;
            target.DropReported = false;
            target.StallReported = false;
            ExportsLogged++;
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "Vulkan", $"Shared GPU images exported for '{target.Surface.Id}': {set}");
        }
        else
        {
            Debug.WriteLine($"Vulkan: shared GPU images re-exported for '{target.Surface.Id}': {set}");
        }

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
                return OnSharedImagesHeld(target, compositor);

            // Any exception before the frame is announced gives the image back (ENG-039: a stuck
            // Writing slot would starve the ring for good).
            var completed = false;
            try
            {
                compositor.RenderToExport(VulkanCompositionPlanner.Plan(frame, width, height), shared.Images, write.BufferIndex, write.WaitForConsumerRelease, write.AcquireKey);
                MediaSharedGpuImageFrame ready = shared.Ring.CompleteWrite(write.BufferIndex);
                completed = true;
                target.StarvedSince = 0;
                target.StallReported = false;
                _allocationFailures = 0;
                consumer.OnFrameReady(ready);
                return PresentResult.Presented;
            }
            catch (VulkanException exception) when (exception.IsConsumerStall)
            {
                return ReplaceStalledImages(target, compositor, exception.Message);
            }
            catch (VulkanException exception)
            {
                return ReportRenderFailure(exception, compositor);
            }
            finally
            {
                if (!completed)
                    shared.Ring.AbortWrite(write.BufferIndex);
            }
        }
    }

    /// <summary>
    /// The compositor holds every image (called under <see cref="_renderGate"/>). Briefly, that is
    /// a compositor running behind: the frame is dropped, not failed. Past
    /// <see cref="SharedImageStallTimeout"/> it is not coming back for them (ENG-039: after a GPU
    /// reset frames were dropped silently for good): the images are replaced.
    /// </summary>
    private PresentResult OnSharedImagesHeld(Target target, VulkanOffscreenCompositor compositor)
    {
        var now = Stopwatch.GetTimestamp();
        if (target.StarvedSince == 0)
            target.StarvedSince = now;

        if (Stopwatch.GetElapsedTime(target.StarvedSince, now) < SharedImageStallTimeout)
        {
            if (!target.DropReported)
            {
                target.DropReported = true;
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "Vulkan", $"Shared GPU image frame dropped for '{target.Surface.Id}': the compositor has not released an image yet.");
            }

            return PresentResult.Presented;
        }

        return ReplaceStalledImages(target, compositor, $"the compositor has not released an image for {SharedImageStallTimeout.TotalSeconds:0.#} s");
    }

    /// <summary>
    /// The compositor stopped giving shared images back (called under <see cref="_renderGate"/>):
    /// a lost device is recovered; otherwise the set is retired and the next frame exports a new
    /// one, which a compositor that recreated its context can import again.
    /// </summary>
    private PresentResult ReplaceStalledImages(Target target, VulkanOffscreenCompositor compositor, string why)
    {
        // Once per release: a compositor that is only paused (and gives nothing back) must not
        // make a new set every second; its frames fail, and Core's probe keeps watching the device.
        if (target.ReplacedStalledSet && Volatile.Read(ref target.ReleasedSinceReplacement) == 0)
            return PresentResult.Failed;

        target.StarvedSince = 0;

        if (compositor.ProbeDeviceLost(out var reason))
        {
            EnterDeviceLost(compositor, $"{why}; {reason}", raise: true);
            return PresentResult.DeviceLost;
        }

        target.ReplacedStalledSet = true;
        Volatile.Write(ref target.ReleasedSinceReplacement, 0);
        lock (_sync)
        {
            if (IsCurrentNoLock(target))
                RetireSharedNoLock(target);
        }

        if (!target.StallReported)
        {
            target.StallReported = true;
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Vulkan", $"Shared GPU images for '{target.Surface.Id}' replaced: {why}.");
        }

        return PresentResult.Failed;
    }

    private void RetireSharedNoLock(Target target, bool destroyImages = true)
    {
        SharedTarget? shared = target.Shared;
        target.Shared = null;
        if (shared is null)
            return;

        shared.Set.Retire();
        if (destroyImages)
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

        // Set by the release callback (ENG-039): the compositor gave an image back since the last
        // stalled set was replaced.
        public int ReleasedSinceReplacement;

        public MediaPresentationSurfaceDescriptor Surface { get; set; } = surface;
        public IMediaPresentationFrameSink? Sink { get; set; }
        public IMediaSharedGpuImageConsumer? Consumer { get; set; }
        public SharedTarget? Shared { get; set; }
        public bool DropReported { get; set; }

        // ENG-039 (render gate): when every image was first found held (Stopwatch timestamp,
        // 0 = not held), and whether replacing a stalled set was reported since the last frame.
        public long StarvedSince { get; set; }
        public bool StallReported { get; set; }
        public bool ReplacedStalledSet { get; set; }

        // G6 (both locks): the consumer and selection of the last logged export.
        public IMediaSharedGpuImageConsumer? LoggedConsumer { get; set; }
        public MediaSharedGpuImageSelection? LoggedSelection { get; set; }
    }

    private sealed class SharedTarget(VulkanExportedImageSet images, MediaSharedGpuImageSet set, MediaSharedGpuImageRing ring)
    {
        public const int BufferCount = 2;

        public VulkanExportedImageSet Images { get; } = images;
        public MediaSharedGpuImageSet Set { get; } = set;
        public MediaSharedGpuImageRing Ring { get; } = ring;
    }
}
