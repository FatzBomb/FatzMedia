using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Graphics.Composition;

namespace FatzBomb.MediaControl.Rendering.Software;

internal sealed class SoftwareRendererBackend : IMediaRendererBackend
{
    private readonly Lock _sync = new();
    private readonly SoftwareCompositionRenderer _renderer = new();
    // One reusable composition buffer (Stage 022A slice 2). A present takes it out and puts it
    // back, so concurrent presents (video and CDG/subtitle threads) never share it and no lock
    // is held across the sink call; a concurrent present simply renders into a new buffer.
    private SoftwareCompositionBuffer? _renderBuffer;
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
        // The software renderer is stateless; each composition frame carries its
        // effective stretch mode, so there is no backend-level value to retain.
    }

    public bool PresentNativeFrame(VideoFrameEventArgs frame) => false;

    public void ClearCompositionBackgroundFrame()
    {
        // No retained background resource exists. PresentComposition renders only
        // from the frame supplied by Core.
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

        SoftwareCompositionBuffer? buffer = Interlocked.Exchange(ref _renderBuffer, null);
        SoftwareRenderOutput output = _renderer.Render(frame, width, height, ref buffer);
        if (output.IsRendered)
        {
            // The sink copies the span before returning, so the buffer can be reused next frame.
            sink.PresentFrame(output.Pixels.Span, output.Width, output.Height, output.Stride);
        }

        ReturnRenderBuffer(buffer);
        if (!output.IsRendered)
            return false;

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

        ReleaseRenderBuffer();
    }

    private void ReleaseRenderBuffer() => _ = Interlocked.Exchange(ref _renderBuffer, null);

    private void ReturnRenderBuffer(SoftwareCompositionBuffer? buffer)
    {
        bool keep;
        lock (_sync)
            keep = !_disposed && IsInitialized;

        // After cleanup or dispose the buffer is dropped, so teardown frees it.
        if (keep && buffer != null)
            _ = Interlocked.CompareExchange(ref _renderBuffer, buffer, null);
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

        ReleaseRenderBuffer();
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
