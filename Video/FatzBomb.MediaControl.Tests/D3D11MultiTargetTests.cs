using System.Collections.Concurrent;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Core.Graphics.D3D.Device;
using FatzBomb.MediaControl.Core.Graphics.D3D.Diagnostics;
using FatzBomb.MediaControl.Core.Graphics.D3D.Presentation;
using FatzBomb.MediaControl.Core.Graphics.D3D.Presentation.DirectComposition;
using FatzBomb.MediaControl.Core.Graphics.D3D.Presentation.Managed;
using FatzBomb.MediaControl.Core.Graphics.D3D.Presentation.NativeHwnd;
using FatzBomb.MediaControl.Core.Graphics.D3D.SwapChain;
using FatzBomb.MediaControl.Rendering.Direct3D11;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// Stage 027A slice 5: Direct3D11 offscreen secondary targets (a managed frame sink or shared
/// textures per secondary surface) beside an offscreen primary. The device tests use WARP, which
/// ships with Windows, so they run on GPU-less CI machines too. The Avalonia import of the shared
/// textures is a manual smoke check (docs/roadmap/updates/027A_5).
/// </summary>
public sealed class D3D11MultiTargetTests
{
    private const string SecondaryId = "singer";

    // ConfiguredColor 0xFF336699 composes to BGRA (0x99, 0x66, 0x33, 0xFF).
    private static readonly MediaCompositionFrame BlueGrey = MediaCompositionFrame.Empty(MediaCompositionClearMode.ConfiguredColor, 0xFF336699);
    private static readonly byte[] BlueGreyBgra = [0x99, 0x66, 0x33, 0xFF];

    [Theory]
    [InlineData("native-hwnd-secondary:singer", "singer")]
    [InlineData("offscreen-secondary:singer", "singer")]
    [InlineData("singer", "singer")]
    public void SecondaryTargetIds_MapBackToTheirSurfaceIds(string targetId, string surfaceId) =>
        Assert.Equal(surfaceId, D3D11PresentationBackendSet.ToSecondarySurfaceId(targetId));

    [Fact]
    public void SecondaryOffscreenTarget_WithoutADevice_IsNotKept()
    {
        using D3D11PresentationBackendSet backends = CreateInactiveBackendSet();
        using var deviceResources = new D3D11DeviceResources(Direct3D11DeviceSelection.Warp);

        Assert.False(backends.CreateSecondaryManagedFrameSinkTarget(deviceResources, SecondaryId, new CopyingFrameSink(), 8, 2));
        Assert.Empty(backends.SecondaryOffscreenTargets);
        Assert.Empty(backends.EnumerateActiveTargetBackends());
        Assert.False(backends.RemoveSecondaryTarget(SecondaryId));
    }

    [Fact]
    public void PrimaryAndSecondaryFrameSinks_EachReceiveTheFrameAtTheirOwnSize()
    {
        using NativeD3D11PresentationBridge bridge = CreateWarpBridge();
        var primarySink = new CopyingFrameSink();
        var secondarySink = new CopyingFrameSink();
        using var primaryHandle = new MediaPresentationFrameSinkHandle(primarySink);
        using var secondaryHandle = new MediaPresentationFrameSinkHandle(secondarySink);
        MediaPresentationSurfaceDescriptor primary = Primary(primaryHandle.CreateSurfaceDescriptor(), 4, 4);
        MediaPresentationSurfaceDescriptor secondary = Secondary(secondaryHandle.CreateSurfaceDescriptor(), 8, 2);

        Assert.True(bridge.AttachSurface(primary));
        Assert.True(bridge.AttachSurface(secondary));
        Assert.True(bridge.PresentComposition(BlueGrey), bridge.LastPresentFailureReason);

        Assert.Equal((4, 4), (primarySink.Width, primarySink.Height));
        Assert.Equal((8, 2), (secondarySink.Width, secondarySink.Height));
        AssertUniform(primarySink.Pixels, BlueGreyBgra);
        AssertUniform(secondarySink.Pixels, BlueGreyBgra);

        // Resizing the secondary leaves the primary alone.
        Assert.True(bridge.ResizeSurface(secondary.WithSize(5, 7)));
        Assert.True(bridge.PresentComposition(BlueGrey), bridge.LastPresentFailureReason);
        Assert.Equal((4, 4), (primarySink.Width, primarySink.Height));
        Assert.Equal((5, 7), (secondarySink.Width, secondarySink.Height));
        Assert.Equal(2, secondarySink.Frames);

        // Detaching the primary keeps the secondary presenting, and the other way round. (A new
        // primary target also presents once, cleared to black, when it is created.)
        Assert.True(bridge.DetachSurface(primary));
        var primaryFrames = primarySink.Frames;
        Assert.True(bridge.PresentComposition(BlueGrey), bridge.LastPresentFailureReason);
        Assert.Equal((primaryFrames, 3), (primarySink.Frames, secondarySink.Frames));
        Assert.True(bridge.AttachSurface(primary));
        Assert.True(bridge.DetachSurface(secondary));
        primaryFrames = primarySink.Frames;
        Assert.True(bridge.PresentComposition(BlueGrey), bridge.LastPresentFailureReason);
        Assert.Equal((primaryFrames + 1, 3), (primarySink.Frames, secondarySink.Frames));
        AssertUniform(primarySink.Pixels, BlueGreyBgra);
    }

    [Fact]
    public void SecondarySharedTextures_AreExportedAtTheSecondarySize()
    {
        using NativeD3D11PresentationBridge bridge = CreateWarpBridge();
        var primarySink = new CopyingFrameSink();
        using var primaryHandle = new MediaPresentationFrameSinkHandle(primarySink);
        var consumer = new RecordingConsumer(new MediaSharedGpuImageRequest(
        [
            new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, MediaSharedGpuImageSyncKinds.KeyedMutex),
            new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.D3D11TextureGlobalSharedHandle, MediaSharedGpuImageSyncKinds.KeyedMutex)
        ]));
        using var consumerHandle = new MediaSharedGpuImageConsumerHandle(consumer);
        MediaPresentationSurfaceDescriptor secondary = Secondary(consumerHandle.CreateSurfaceDescriptor(), 9, 5);

        Assert.True(bridge.AttachSurface(Primary(primaryHandle.CreateSurfaceDescriptor(), 4, 4)));
        Assert.True(bridge.AttachSurface(secondary), string.Join("; ", consumer.Unavailable));
        MediaSharedGpuImageSet set = Assert.Single(consumer.Sets);
        Assert.Equal((9, 5, 2), (set.Width, set.Height, set.Buffers.Count));
        Assert.Equal(MediaSharedGpuImageSyncKinds.KeyedMutex, set.SyncKind);

        var primaryFrames = primarySink.Frames;
        Assert.True(bridge.PresentComposition(BlueGrey), bridge.LastPresentFailureReason);
        Assert.Equal(primaryFrames + 1, primarySink.Frames);
        MediaSharedGpuImageFrame frame = Assert.Single(consumer.Frames);
        Assert.Equal(set.Generation, frame.Generation);

        // A resize exports a new pair for the secondary only; a detach retires it.
        Assert.True(bridge.ResizeSurface(secondary.WithSize(12, 6)));
        Assert.Equal((12, 6), (consumer.Sets.Last().Width, consumer.Sets.Last().Height));
        Assert.True(set.IsRetired);
        set.CompleteImport(handlesConsumed: false);
        Assert.True(bridge.DetachSurface(secondary));
        Assert.True(consumer.Sets.Last().IsRetired);
        consumer.Sets.Last().CompleteImport(handlesConsumed: false);
        Assert.Empty(consumer.Unavailable);
    }

    private static NativeD3D11PresentationBridge CreateWarpBridge()
    {
        var bridge = new NativeD3D11PresentationBridge(
            new MediaPlayerConfiguration(),
            new Direct3D11PresentationOptions { DeviceSelection = Direct3D11DeviceSelection.Warp });
        Assert.True(bridge.Initialize());
        return bridge;
    }

    private static D3D11PresentationBackendSet CreateInactiveBackendSet()
    {
        var configuration = new MediaPlayerConfiguration();
        var diagnostics = new D3D11PresentationDiagnostics(configuration);
        return new D3D11PresentationBackendSet(
            new D3D11NativeHwndSwapChainBackend(configuration, NativeSwapChainPresentMode.VSync, diagnostics, () => { }, () => { }),
            new D3D11DirectCompositionSingleTargetBackend(configuration, NativeSwapChainPresentMode.VSync, diagnostics, () => { }),
            new D3D11ManagedFrameSinkBackend());
    }

    private static MediaPresentationSurfaceDescriptor Primary(MediaNativeSurfaceDescriptor native, int width, int height) =>
        new(MediaPresentationSurfaceDescriptor.PrimarySurfaceId, MediaPresentationSurfaceRole.Primary, native, width, height);

    private static MediaPresentationSurfaceDescriptor Secondary(MediaNativeSurfaceDescriptor native, int width, int height) =>
        new(SecondaryId, MediaPresentationSurfaceRole.Projector, native, width, height);

    private static void AssertUniform(byte[] pixels, byte[] expected)
    {
        Assert.NotEmpty(pixels);
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            for (var channel = 0; channel < 4; channel++)
                Assert.InRange(pixels[offset + channel], expected[channel] - 1, expected[channel] + 1);
        }
    }

    private sealed class CopyingFrameSink : IMediaPresentationFrameSink
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Frames { get; private set; }
        public byte[] Pixels { get; private set; } = [];

        public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
        {
            (Width, Height) = (width, height);
            Pixels = bgraPixels[..(stride * height)].ToArray();
            Frames++;
        }
    }

    private sealed class RecordingConsumer(MediaSharedGpuImageRequest request) : IMediaSharedGpuImageConsumer
    {
        public ConcurrentQueue<MediaSharedGpuImageSet> Sets { get; } = new();
        public ConcurrentQueue<MediaSharedGpuImageFrame> Frames { get; } = new();
        public ConcurrentQueue<string> Unavailable { get; } = new();

        public MediaSharedGpuImageRequest Request { get; } = request;

        public void OnImagesCreated(MediaSharedGpuImageSet images) => Sets.Enqueue(images);

        public void OnFrameReady(MediaSharedGpuImageFrame frame) => Frames.Enqueue(frame);

        public void OnUnavailable(string reason) => Unavailable.Enqueue(reason);
    }
}
