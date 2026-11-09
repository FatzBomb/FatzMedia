using System.Runtime.InteropServices;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Graphics;
using FFmpeg.AutoGen;

namespace FatzMedia.Video;

/// <summary>Windows FFmpeg hardware-device policy and D3D11 native-frame extraction.</summary>
sealed unsafe class WindowsVideoHardwareAccelerationServices : IVideoHardwareAccelerationServices
{
    static readonly string[] PreferredDeviceTypes = ["d3d11va", "d3d12va", "dxva2"];

    public IReadOnlyList<string> PreferredDeviceTypeNames => PreferredDeviceTypes;

    public bool IsPreferredPixelFormat(string deviceTypeName, int pixelFormat) => deviceTypeName switch
    {
        "d3d11va" => (AVPixelFormat)pixelFormat == AVPixelFormat.AV_PIX_FMT_D3D11,
        "d3d12va" or "dxva2" => true,
        _ => false
    };

    public NativeVideoDeviceResource? CreateDeviceResource(
        string deviceTypeName,
        nint ffmpegHardwareDeviceContext)
    {
        if (deviceTypeName != "d3d11va" || ffmpegHardwareDeviceContext == 0)
        {
            return new NativeVideoDeviceResource(new MediaGraphicsBackendId(deviceTypeName), 0);
        }

        var buffer = (AVBufferRef*)ffmpegHardwareDeviceContext;
        var deviceContext = (AVHWDeviceContext*)buffer->data;
        if (deviceContext == null || deviceContext->hwctx == null)
        {
            return null;
        }

        var d3d11Context = (AVD3D11VADeviceContext*)deviceContext->hwctx;
        return new NativeVideoDeviceResource(
            MediaGraphicsBackendId.Direct3D11,
            (nint)d3d11Context->device,
            (nint)d3d11Context->device_context);
    }

    public NativeVideoFrameResource? CreateFrameResource(
        string deviceTypeName,
        NativeVideoDeviceResource device,
        nint ffmpegFrame)
    {
        if (deviceTypeName != "d3d11va" || ffmpegFrame == 0 || device.DeviceHandle == 0)
        {
            return null;
        }

        var frame = (AVFrame*)ffmpegFrame;
        if ((AVPixelFormat)frame->format != AVPixelFormat.AV_PIX_FMT_D3D11 || frame->data[0] == null)
        {
            return null;
        }

        return new NativeVideoFrameResource(
            device,
            (nint)frame->data[0],
            (int)(nint)frame->data[1]);
    }

    public bool IsDeviceLost(NativeVideoDeviceResource device)
    {
        if (device.BackendId != MediaGraphicsBackendId.Direct3D11 || device.DeviceHandle == 0)
        {
            return false;
        }

        try
        {
            _ = Marshal.AddRef(device.DeviceHandle);
            using var d3d11Device = new Vortice.Direct3D11.ID3D11Device(device.DeviceHandle);
            return d3d11Device.DeviceRemovedReason.Failure;
        }
        catch
        {
            return false;
        }
    }
}
