using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Graphics;
using FatzMedia.Video.Core.Graphics.Composition;

namespace FatzMedia.Video.Software;

sealed class SoftwareRendererBackend : IMediaRendererBackend, IMediaRendererVideoDecodeDevice
{
    /// <summary>Why this renderer offers no hardware decode device.</summary>
    internal const string NoVideoDecodeReason = "the software renderer draws only CPU frames";

    readonly Lock _sync = new();
    // Attached managed frame-sink surfaces by id: the primary plus any secondary/projector
    // windows (ENG-016). Each composition is rendered once per target at the target's size.
    readonly Dictionary<string, Target> _targets = new(StringComparer.OrdinalIgnoreCase);
    bool _disposed;

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
        var outcome = new CompositionPresentOutcome();
        var anyIncluded = false;
        foreach (var target in targets)
        {
            var isPrimary = target.Surface.Role == MediaPresentationSurfaceRole.Primary;
            // A composition for some targets only (display-locked video on several monitors).
            if (!frame.IsForTarget(isPrimary, target.Surface.Id))
                continue;

            anyIncluded = true;
            outcome.Record(isPrimary, Present(frame, target));
        }

        // A filter that matches no attached target (its surface just detached) is nothing to do.
        if (!anyIncluded)
            return frame.Targets is not null;

        var succeeded = outcome.Succeeded;
        if (succeeded)
            FramePresented?.Invoke(this, EventArgs.Empty);
        return succeeded;
    }

    bool Present(MediaCompositionFrame frame, Target target)
    {
        // One reusable composition buffer per target (Stage 022A slice 2). A present takes it
        // out and puts it back, so concurrent presents (video and CDG/subtitle threads) never
        // share it and no lock is held across the sink call; a concurrent present simply renders
        // into a new buffer.
        var buffer = Interlocked.Exchange(ref target.Buffer, null);
        var output = SoftwareCompositionRenderer.Render(frame, target.Surface.PixelWidth, target.Surface.PixelHeight, ref buffer);
        if (output.IsRendered)
        {
            // The sink copies the span before returning, so the buffer can be reused next frame.
            target.Sink.PresentFrame(output.Pixels.Span, output.Width, output.Height, output.Stride);
        }

        bool keep;
        lock (_sync)
            keep = !_disposed && IsInitialized && _targets.TryGetValue(target.Surface.Id, out var current) && ReferenceEquals(current, target);

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

    // A renderer that decodes on no device: while it is attached Core offers decoders no hardware
    // device type, so they open in software instead of producing frames this renderer would drop
    // (ENG-037). This holds whatever the configuration's VideoDecoding asks for.
    public string? VideoDecodeDeviceType => null;

    public string? VideoDecodeUnavailableReason => NoVideoDecodeReason;

    public nint CreateVideoDecodeDeviceContext(out string? failureReason)
    {
        failureReason = NoVideoDecodeReason;
        return 0;
    }

    public NativeVideoDeviceResource? CreateVideoDecodeDeviceResource(nint ffmpegHardwareDeviceContext) => null;

    public NativeVideoFrameResource? CreateVideoDecodeFrameResource(NativeVideoDeviceResource device, nint ffmpegFrame) => null;

    public bool IsVideoDecodeDeviceLost(NativeVideoDeviceResource device) => false;

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

    bool SetSurface(MediaPresentationSurfaceDescriptor surface, bool requireExisting)
    {
        if (!MediaPresentationFrameSinkHandle.TryResolve(surface, out var sink))
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

    sealed class Target(MediaPresentationSurfaceDescriptor surface, IMediaPresentationFrameSink sink)
    {
        public SoftwareCompositionBuffer? Buffer;

        public MediaPresentationSurfaceDescriptor Surface { get; } = surface;
        public IMediaPresentationFrameSink Sink { get; } = sink;
    }
}
