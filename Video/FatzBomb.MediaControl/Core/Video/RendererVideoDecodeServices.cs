using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Graphics;
using FatzBomb.MediaControl.Core.Graphics.Composition;

namespace FatzBomb.MediaControl.Core.Video;

/// <summary>
/// The hardware-decode services every decoder of one player uses (Stage 045A slice 2): the
/// platform's, unless the presentation renderer decodes on its own device
/// (<see cref="IMediaRendererVideoDecodeDevice"/>). While such a renderer is attached it alone
/// supplies the device type, the FFmpeg device context, frame descriptors and device-loss checks,
/// so no decoder produces frames on a device the renderer cannot draw.
/// </summary>
internal sealed class RendererVideoDecodeServices(IVideoHardwareAccelerationServices platform) : IVideoHardwareAccelerationServices
{
    private readonly IVideoHardwareAccelerationServices _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    private readonly Lock _reportGate = new();
    private IMediaRendererVideoDecodeDevice? _renderer;
    private string? _reportedReason;

    /// <summary>Gets the attached renderer device, or null.</summary>
    public IMediaRendererVideoDecodeDevice? Renderer => Volatile.Read(ref _renderer);

    /// <summary>
    /// Attaches the presentation renderer's decode device; null for a renderer that takes the
    /// platform's hardware frames (or none). Decoders opened afterwards follow it.
    /// </summary>
    public void Attach(IMediaRendererVideoDecodeDevice? renderer)
    {
        Volatile.Write(ref _renderer, renderer);
        lock (_reportGate)
            _reportedReason = null;
    }

    /// <summary>Detaches <paramref name="renderer"/> if it is still the attached one.</summary>
    public void Detach(IMediaRendererVideoDecodeDevice renderer) =>
        _ = Interlocked.CompareExchange(ref _renderer, null, renderer);

    public IReadOnlyList<string> PreferredDeviceTypeNames => Renderer is { } renderer
        ? renderer.VideoDecodeDeviceType is { } type ? [type] : []
        : _platform.PreferredDeviceTypeNames;

    public bool IsPreferredPixelFormat(string deviceTypeName, int pixelFormat) =>
        IsRendererType(deviceTypeName, out _) || _platform.IsPreferredPixelFormat(deviceTypeName, pixelFormat);

    /// <summary>
    /// Whether the attached renderer supplies devices of <paramref name="deviceTypeName"/>. Then
    /// <paramref name="context"/> is a new FFmpeg device context on its device, or 0 when none
    /// could be made (the reason is reported once).
    /// </summary>
    public bool TryCreateRendererDeviceContext(string deviceTypeName, out nint context)
    {
        context = 0;
        if (!IsRendererType(deviceTypeName, out IMediaRendererVideoDecodeDevice? renderer))
            return false;

        context = renderer!.CreateVideoDecodeDeviceContext(out var reason);
        if (context == 0)
            ReportUnavailable(reason ?? "the renderer's device could not be shared with FFmpeg");
        return true;
    }

    /// <summary>
    /// Called when a decoder may use hardware: a renderer whose device cannot decode says why,
    /// once per reason, so software decoding is never silent.
    /// </summary>
    public void NoteHardwareDecodingRequested()
    {
        if (Renderer is { VideoDecodeDeviceType: null } renderer)
            ReportUnavailable(renderer.VideoDecodeUnavailableReason ?? "the renderer's device cannot decode video");
    }

    public NativeVideoDeviceResource? CreateDeviceResource(string deviceTypeName, nint ffmpegHardwareDeviceContext) =>
        IsRendererType(deviceTypeName, out IMediaRendererVideoDecodeDevice? renderer)
            ? renderer!.CreateVideoDecodeDeviceResource(ffmpegHardwareDeviceContext)
            : _platform.CreateDeviceResource(deviceTypeName, ffmpegHardwareDeviceContext);

    public NativeVideoFrameResource? CreateFrameResource(string deviceTypeName, NativeVideoDeviceResource device, nint ffmpegFrame) =>
        IsRendererType(deviceTypeName, out IMediaRendererVideoDecodeDevice? renderer)
            ? renderer!.CreateVideoDecodeFrameResource(device, ffmpegFrame)
            : _platform.CreateFrameResource(deviceTypeName, device, ffmpegFrame);

    public bool IsDeviceLost(NativeVideoDeviceResource device) =>
        IsRendererType(device.BackendId.Value, out IMediaRendererVideoDecodeDevice? renderer)
            ? renderer!.IsVideoDecodeDeviceLost(device)
            : _platform.IsDeviceLost(device);

    private bool IsRendererType(string? deviceTypeName, out IMediaRendererVideoDecodeDevice? renderer)
    {
        renderer = Renderer;
        return renderer is not null
            && deviceTypeName is not null
            && string.Equals(renderer.VideoDecodeDeviceType, deviceTypeName, StringComparison.Ordinal);
    }

    private void ReportUnavailable(string reason)
    {
        lock (_reportGate)
        {
            if (_reportedReason == reason)
                return;

            _reportedReason = reason;
        }

        DiagnosticRouter.Raise(
            MediaDiagnosticLevel.Warning,
            "VideoDecoder",
            $"Hardware decoding on the renderer's device is unavailable ({reason}); decoding in software.");
    }
}
