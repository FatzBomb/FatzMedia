using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Graphics.Composition;

namespace FatzBomb.MediaControl.Rendering.Software;

internal sealed class SoftwareRendererBackend : IMediaRendererBackend
{
    private readonly Lock _sync = new();
    private readonly SoftwareCompositionRenderer _renderer = new();
    private MediaPresentationSurfaceDescriptor? _surface;
    private IMediaPresentationFrameSink? _sink;
    private bool _disposed;

    public event EventHandler? FramePresented;

    public string BackendName => "Software";
    public string DeviceLostDiagnosticCategory => "Software.DeviceLost";
    public string DeviceLostRecoveryFailedDiagnosticCategory => "Software.DeviceLost.RecoveryFailed";
    public bool IsInitialized { get; private set; }
    public nint NativeDeviceHandle => 0;

    public bool Initialize()
    {
        lock (_sync)
        {
            if (_disposed)
                return false;

            IsInitialized = true;
            return true;
        }
    }

    public bool AttachSurface(MediaPresentationSurfaceDescriptor surface) => SetSurface(surface, requireExisting: false);

    public bool ResizeSurface(MediaPresentationSurfaceDescriptor surface) => SetSurface(surface, requireExisting: true);

    public bool SetSurfaceLiveResize(MediaPresentationSurfaceDescriptor surface, bool isResizing)
    {
        lock (_sync)
        {
            return !_disposed && _surface?.Id == surface.Id;
        }
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
    }

    public bool PresentNativeFrame(VideoFrameEventArgs frame) => false;

    public void ClearCompositionBackgroundFrame()
    {
    }

    public bool PresentComposition(MediaCompositionFrame frame)
    {
        IMediaPresentationFrameSink? sink;
        int width;
        int height;
        lock (_sync)
        {
            if (_disposed || !IsInitialized || _surface == null || _sink == null)
                return false;

            sink = _sink;
            width = _surface.PixelWidth;
            height = _surface.PixelHeight;
        }

        SoftwareRenderOutput output = _renderer.Render(frame, width, height);
        if (!output.IsRendered)
            return false;

        sink.PresentFrame(output.Pixels.Span, output.Width, output.Height, output.Stride);
        FramePresented?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void CleanupResources()
    {
        lock (_sync)
        {
            _surface = null;
            _sink = null;
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
            IsInitialized = false;
        }
    }

    private bool SetSurface(MediaPresentationSurfaceDescriptor surface, bool requireExisting)
    {
        if (!TryGetManagedFrameSink(surface, out IMediaPresentationFrameSink? sink))
            return false;

        lock (_sync)
        {
            if (_disposed || (requireExisting && _surface?.Id != surface.Id))
                return false;

            IsInitialized = true;
            _surface = surface;
            _sink = sink;
            return true;
        }
    }

    private static bool TryGetManagedFrameSink(
        MediaPresentationSurfaceDescriptor? surface,
        out IMediaPresentationFrameSink? sink)
    {
        sink = null;
        return surface is { IsValid: true, Role: MediaPresentationSurfaceRole.Primary }
            && surface.NativeSurface.SurfaceTypeId == MediaNativeSurfaceTypeIds.ManagedBgraFrameSink
            && surface.NativeSurface.TryGetHandle(MediaNativeHandleIds.FrameSink, out nint handle)
            && MediaPresentationFrameSinkHandle.TryResolve(handle, out sink)
            && sink != null;
    }
}
