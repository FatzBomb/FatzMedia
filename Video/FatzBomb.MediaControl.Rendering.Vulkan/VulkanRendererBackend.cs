using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Graphics.Composition;

namespace FatzBomb.MediaControl.Rendering.Vulkan;

/// <summary>
/// Vulkan renderer backend, vertical slice 1: one primary managed BGRA frame-sink surface,
/// GPU clear plus background blit, CPU readback into the sink. Window-system swapchains,
/// shader-based overlay composition, native video resources, and multi-target fan-out are
/// later slices; frames that need them are rejected with a diagnostic instead of being drawn
/// incorrectly.
/// </summary>
internal sealed class VulkanRendererBackend(Func<VulkanOffscreenCompositor?> compositorFactory) : IMediaRendererBackend
{
    private readonly Lock _sync = new();
    private VulkanOffscreenCompositor? _compositor;
    private MediaPresentationSurfaceDescriptor? _surface;
    private IMediaPresentationFrameSink? _sink;
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

            _compositor = compositorFactory();
            IsInitialized = _compositor is not null;
            return IsInitialized;
        }
    }

    public bool AttachSurface(MediaPresentationSurfaceDescriptor surface) => SetSurface(surface, requireExisting: false);

    public bool ResizeSurface(MediaPresentationSurfaceDescriptor surface) => SetSurface(surface, requireExisting: true);

    public bool SetSurfaceLiveResize(MediaPresentationSurfaceDescriptor surface, bool isResizing)
    {
        lock (_sync)
            return !_disposed && _surface?.Id == surface.Id;
    }

    public bool DetachSurface(MediaPresentationSurfaceDescriptor surface)
    {
        lock (_sync)
        {
            if (_disposed || _surface?.Id != surface.Id)
                return false;

            _surface = null;
            _sink = null;
            return true;
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
            if (_disposed || _surface is null || _sink is null || !Initialize())
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

        try
        {
            VulkanBlit? blit = frame.HasBackground
                ? VulkanCompositionPlanner.PlanBackground(frame.BackgroundWidth, frame.BackgroundHeight, width, height, frame.EffectiveBackgroundStretchMode)
                : null;
            var pixels = compositor!.Render(
                width,
                height,
                VulkanCompositionPlanner.ResolveClear(frame),
                frame.HasBackground ? frame.BackgroundBgra.Span : [],
                frame.BackgroundWidth,
                frame.BackgroundHeight,
                frame.BackgroundStride,
                blit);
            sink!.PresentFrame(pixels, width, height, width * 4);
            FramePresented?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (VulkanException exception)
        {
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Error,
                exception.IsDeviceLost ? DeviceLostDiagnosticCategory : "Vulkan",
                exception.Message,
                innerException: exception);
            if (exception.IsDeviceLost)
                CleanupResources();
            return false;
        }
    }

    public void CleanupResources()
    {
        lock (_sync)
        {
            _compositor?.Dispose();
            _compositor = null;
            IsInitialized = false;
        }
    }

    public bool EnsureNativeFrameDevice(VideoFrameEventArgs frame, out bool deviceAdopted)
    {
        deviceAdopted = false;
        return false;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _surface = null;
            _sink = null;
            _compositor?.Dispose();
            _compositor = null;
            IsInitialized = false;
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
            $"Vulkan preview slice cannot draw this frame yet ({support}); only clear and software BGRA backgrounds are supported.");
    }

    private bool SetSurface(MediaPresentationSurfaceDescriptor surface, bool requireExisting)
    {
        if (!TryGetManagedFrameSink(surface, out IMediaPresentationFrameSink? sink))
            return false;

        lock (_sync)
        {
            if (_disposed || (requireExisting && _surface?.Id != surface.Id))
                return false;

            _surface = surface;
            _sink = sink;
            return true;
        }
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
