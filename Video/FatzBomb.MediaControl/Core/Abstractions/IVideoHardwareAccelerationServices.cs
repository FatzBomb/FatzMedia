using FatzBomb.MediaControl.Core.Graphics;

namespace FatzBomb.MediaControl.Core.Abstractions;

/// <summary>
/// Supplies platform-specific FFmpeg hardware-device policy and native-frame extraction.
/// Core continues to own decoding, frame queues, fallback, and resource lifetime.
/// </summary>
public interface IVideoHardwareAccelerationServices
{
    IReadOnlyList<string> PreferredDeviceTypeNames { get; }

    bool IsPreferredPixelFormat(string deviceTypeName, int pixelFormat);

    NativeVideoDeviceResource? CreateDeviceResource(
        string deviceTypeName,
        nint ffmpegHardwareDeviceContext);

    NativeVideoFrameResource? CreateFrameResource(
        string deviceTypeName,
        NativeVideoDeviceResource device,
        nint ffmpegFrame);

    bool IsDeviceLost(NativeVideoDeviceResource device);
}

/// <summary>Portable policy that disables hardware decoding and always uses CPU frames.</summary>
public sealed class SoftwareOnlyVideoHardwareAccelerationServices : IVideoHardwareAccelerationServices
{
    public static SoftwareOnlyVideoHardwareAccelerationServices Instance { get; } = new();

    public IReadOnlyList<string> PreferredDeviceTypeNames => [];

    public bool IsPreferredPixelFormat(string deviceTypeName, int pixelFormat) => false;

    public NativeVideoDeviceResource? CreateDeviceResource(string deviceTypeName, nint ffmpegHardwareDeviceContext) => null;

    public NativeVideoFrameResource? CreateFrameResource(
        string deviceTypeName,
        NativeVideoDeviceResource device,
        nint ffmpegFrame) => null;

    public bool IsDeviceLost(NativeVideoDeviceResource device) => false;
}
