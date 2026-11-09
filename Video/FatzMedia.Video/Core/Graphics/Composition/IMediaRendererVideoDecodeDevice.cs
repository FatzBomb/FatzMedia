namespace FatzMedia.Video.Core.Graphics.Composition;

/// <summary>
/// Optional on an <see cref="IMediaRendererBackend"/> (Stage 045A slice 2): the renderer decodes
/// video on its own device. Core then asks it, not the platform, for the FFmpeg hardware device,
/// so decoded frames are already on the device that draws them. This is the reverse of
/// Direct3D11, whose renderer adopts the decoder's device.
/// </summary>
/// <remarks>
/// A renderer that implements this takes only hardware frames decoded on its device: while it is
/// attached, Core offers no other hardware device type, so a renderer whose device cannot decode
/// gets software frames. Handles are opaque to Core: FFmpeg <c>AVBufferRef*</c> and
/// <c>AVFrame*</c> pointers and native device handles. Members are called from decoder threads.
/// </remarks>
public interface IMediaRendererVideoDecodeDevice
{
    /// <summary>
    /// Gets the FFmpeg hardware device type the renderer's device decodes with (for example
    /// <c>vulkan</c>), or null when it cannot decode (see <see cref="VideoDecodeUnavailableReason"/>).
    /// </summary>
    string? VideoDecodeDeviceType { get; }

    /// <summary>Gets why the renderer's device cannot decode, or null when it can.</summary>
    string? VideoDecodeUnavailableReason { get; }

    /// <summary>
    /// Creates a new reference to an initialized FFmpeg hardware device context
    /// (<c>AVBufferRef*</c>) on the renderer's current device, which the caller releases with
    /// <c>av_buffer_unref</c>. Returns 0 and the reason when the device is unavailable or FFmpeg
    /// refused it.
    /// </summary>
    nint CreateVideoDecodeDeviceContext(out string? failureReason);

    /// <summary>Describes the device behind a context from <see cref="CreateVideoDecodeDeviceContext"/>.</summary>
    NativeVideoDeviceResource? CreateVideoDecodeDeviceResource(nint ffmpegHardwareDeviceContext);

    /// <summary>
    /// Describes a decoded hardware frame (an <c>AVFrame*</c> Core retains until the next frame
    /// is presented), or returns null when the renderer cannot draw it.
    /// </summary>
    NativeVideoFrameResource? CreateVideoDecodeFrameResource(NativeVideoDeviceResource device, nint ffmpegFrame);

    /// <summary>Whether the device behind <paramref name="device"/> was lost.</summary>
    bool IsVideoDecodeDeviceLost(NativeVideoDeviceResource device);
}
