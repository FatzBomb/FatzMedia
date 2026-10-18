using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Graphics.Composition;

namespace FatzBomb.MediaControl.Rendering.Software;

internal sealed class SoftwareRendererBackend : IMediaRendererBackend
{
    private readonly Lock _sync = new();
    private readonly SoftwareCompositionRenderer _renderer = new();
    // Attached managed frame-sink surfaces by id: the primary plus any secondary/projector
    // windows (ENG-016). Each composition is rendered once per target at the target's size.
    private readonly Dictionary<string, Target> _targets = new(StringComparer.OrdinalIgnoreCase);
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
            return !_disposed && _targets.ContainsKey(surface.Id);
        }
    }

    public bool DetachSurface(MediaPresentationSurfaceDescriptor surface)
    {
        lock (_sync)
        {
            return !_disposed && _targets.Remove(surface.Id);
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
        Target[] targets;
        lock (_sync)
        {
            if (_disposed || !IsInitialized || _targets.Count == 0)
                return false;

            targets = [.. _targets.Values];
        }

        // As Direct3D11 does: with a primary attached, success is the primary's present, so a
        // secondary that presented never hides a failed primary frame.
        var primaryExpected = false;
        var primaryPresented = false;
        var anyPresented = false;
        foreach (Target target in targets)
        {
            var isPrimary = target.Surface.Role == MediaPresentationSurfaceRole.Primary;
            var presented = Present(frame, target);
            primaryExpected |= isPrimary;
            primaryPresented |= isPrimary && presented;
            anyPresented |= presented;
        }

        var succeeded = primaryExpected ? primaryPresented : anyPresented;
        if (succeeded)
            FramePresented?.Invoke(this, EventArgs.Empty);
        return succeeded;
    }

    private bool Present(MediaCompositionFrame frame, Target target)
    {
        // One reusable composition buffer per target (Stage 022A slice 2). A present takes it
        // out and puts it back, so concurrent presents (video and CDG/subtitle threads) never
        // share it and no lock is held across the sink call; a concurrent present simply renders
        // into a new buffer.
        SoftwareCompositionBuffer? buffer = Interlocked.Exchange(ref target.Buffer, null);
        SoftwareRenderOutput output = _renderer.Render(frame, target.Surface.PixelWidth, target.Surface.PixelHeight, ref buffer);
        if (output.IsRendered)
        {
            // The sink copies the span before returning, so the buffer can be reused next frame.
            target.Sink.PresentFrame(output.Pixels.Span, output.Width, output.Height, output.Stride);
        }

        bool keep;
        lock (_sync)
            keep = !_disposed && IsInitialized && _targets.TryGetValue(target.Surface.Id, out Target? current) && ReferenceEquals(current, target);

        // A detached, resized (replaced), cleaned-up or disposed target drops its buffer.
        if (keep && buffer != null)
            _ = Interlocked.CompareExchange(ref target.Buffer, buffer, null);

        return output.IsRendered;
    }

    public void CleanupResources()
    {
        lock (_sync)
        {
            _targets.Clear();
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
            _targets.Clear();
            IsInitialized = false;
        }
    }

    private bool SetSurface(MediaPresentationSurfaceDescriptor surface, bool requireExisting)
    {
        if (!TryGetManagedFrameSink(surface, out IMediaPresentationFrameSink? sink))
            return false;

        lock (_sync)
        {
            if (_disposed || (requireExisting && !_targets.ContainsKey(surface.Id)))
                return false;

            IsInitialized = true;
            _targets[surface.Id] = new Target(surface, sink!);
            return true;
        }
    }

    private static bool TryGetManagedFrameSink(
        MediaPresentationSurfaceDescriptor? surface,
        out IMediaPresentationFrameSink? sink)
    {
        sink = null;
        return surface is { IsValid: true }
            && surface.NativeSurface.SurfaceTypeId == MediaNativeSurfaceTypeIds.ManagedBgraFrameSink
            && surface.NativeSurface.TryGetHandle(MediaNativeHandleIds.FrameSink, out nint handle)
            && MediaPresentationFrameSinkHandle.TryResolve(handle, out sink)
            && sink != null;
    }

    private sealed class Target(MediaPresentationSurfaceDescriptor surface, IMediaPresentationFrameSink sink)
    {
        public SoftwareCompositionBuffer? Buffer;

        public MediaPresentationSurfaceDescriptor Surface { get; } = surface;
        public IMediaPresentationFrameSink Sink { get; } = sink;
    }
}
