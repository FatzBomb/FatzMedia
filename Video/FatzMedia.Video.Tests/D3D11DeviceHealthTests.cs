using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Graphics;
using FatzMedia.Video.Core.Graphics.Composition;
using FatzMedia.Video.Core.Graphics.D3D.Device;
using FatzMedia.Video.Core.Graphics.D3D.Diagnostics;
using FatzMedia.Video.Direct3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FatzMedia.Video.Tests;

/// <summary>
/// ENG-039: the Direct3D11 device probe Core polls, and the refusal of a decoder device the
/// renderer cannot use. WARP cannot be reset, so a removed device is a manual smoke check
/// (<c>dxcap -forcetdr</c>, docs/roadmap/updates/ENG-039).
/// </summary>
public sealed class D3D11DeviceHealthTests
{
    private const int Width = 16;
    private const int Height = 8;

    // ConfiguredColor 0xFF336699 composes to BGRA (0x99, 0x66, 0x33, 0xFF).
    private static readonly MediaCompositionFrame BlueGrey = MediaCompositionFrame.Empty(MediaCompositionClearMode.ConfiguredColor, 0xFF336699);

    [Fact]
    public void ProbeDeviceLost_HealthyWarpDevice_IsNotLost()
    {
        using NativeD3D11PresentationBridge bridge = D3D11MultiTargetTests.CreateWarpBridge();

        Assert.False(bridge.ProbeDeviceLost(out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void ProbeDeviceLost_WithoutADevice_IsNotLost()
    {
        var bridge = new NativeD3D11PresentationBridge(
            new MediaPlayerConfiguration(),
            new Direct3D11PresentationOptions { DeviceSelection = Direct3D11DeviceSelection.Warp });
        Assert.False(bridge.ProbeDeviceLost(out _));

        Assert.True(bridge.Initialize());
        bridge.CleanupResources();
        Assert.False(bridge.ProbeDeviceLost(out _));

        bridge.Dispose();
        Assert.False(bridge.ProbeDeviceLost(out _));
    }

    [Fact]
    public void ProbeDeviceLost_FollowsAnAdoptedDecoderDevice()
    {
        var resources = new D3D11DeviceResources(Direct3D11DeviceSelection.Warp);
        using var decoder = new D3D11DeviceResources(Direct3D11DeviceSelection.Warp);
        Assert.True(decoder.Initialize(new MediaPlayerConfiguration()));

        Assert.True(resources.InitializeFromNativeDevice(decoder.Device!.NativePointer, decoder.Context!.NativePointer, new MediaPlayerConfiguration()));
        Assert.False(resources.ProbeDeviceRemoved(out _));

        // Released with the device: the probe never reads a disposed one.
        resources.Cleanup();
        Assert.False(resources.ProbeDeviceRemoved(out _));
    }

    [Fact]
    public void DescribeStepFailure_NamesTheStep()
    {
        var message = D3D11DeviceLostClassifier.DescribeStepFailure("video processor input view", new InvalidOperationException("boom"), device: null);

        Assert.Equal("video processor input view failed: InvalidOperationException: boom", message);
    }

    [Fact]
    public void EnsureNativeFrameDevice_RefusesADeviceWithoutVideoSupport_WithoutCleaningUp()
    {
        using NativeD3D11PresentationBridge bridge = D3D11MultiTargetTests.CreateWarpBridge();
        var sink = new D3D11MultiTargetTests.CopyingFrameSink();
        using var handle = new MediaPresentationFrameSinkHandle(sink);
        Assert.True(bridge.AttachSurface(new MediaPresentationSurfaceDescriptor(
            MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
            MediaPresentationSurfaceRole.Primary,
            handle.CreateSurfaceDescriptor(),
            Width,
            Height)));
        var ownDevice = bridge.NativeDeviceHandle;

        // A WARP device created without video support stands in for a decoder device the renderer
        // cannot draw from. Where WARP offers the video device anyway, there is nothing to refuse.
        SharpGen.Runtime.Result created = D3D11.D3D11CreateDevice(
            adapter: null,
            DriverType.Warp,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
            out ID3D11Device? device,
            out _,
            out ID3D11DeviceContext? context);
        Assert.True(created.Success);
        using (device)
        using (context)
        {
            if (D3D11DeviceResources.HasVideoSupport(device!.NativePointer, context!.NativePointer))
                return;

            using ID3D11Texture2D texture = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, Width, Height, 1, 1));
            var frame = new VideoFrameEventArgs
            {
                Width = Width,
                Height = Height,
                Stride = 0,
                PtsSeconds = 0,
                FramesPerSecond = 30,
                NativeResource = new NativeVideoFrameResource(
                    new NativeVideoDeviceResource(MediaGraphicsBackendId.Direct3D11, device.NativePointer, context.NativePointer),
                    texture.NativePointer,
                    SubresourceIndex: 0)
            };

            Assert.False(bridge.EnsureNativeFrameDevice(frame, out var adopted));
            Assert.False(adopted);
        }

        // The renderer kept its device and its surface.
        Assert.Equal(ownDevice, bridge.NativeDeviceHandle);
        var frames = sink.Frames;
        Assert.True(bridge.PresentComposition(BlueGrey), bridge.LastPresentFailureReason);
        Assert.Equal(frames + 1, sink.Frames);
    }
}
