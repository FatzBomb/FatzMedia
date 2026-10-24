using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Core.Graphics.D3D.Presentation;
using FatzBomb.MediaControl.Rendering.Direct3D11;
using Vortice.Direct3D11;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// ENG-026: Direct3D11 per-frame costs. Render-target views are cached per target texture, the
/// managed frame sink keeps its readback buffer through resize churn, and the post-process
/// textures of removed secondary windows are released. WARP, so these run on GPU-less Windows CI.
/// Native-window resizes (queued ResizeBuffers for secondary windows) need a real HWND and are a
/// manual smoke check (docs/roadmap/ENGINEERING_ISSUES.md, ENG-026).
/// </summary>
public sealed class D3D11RendererCostTests
{
    // ConfiguredColor 0xFF336699 composes to BGRA (0x99, 0x66, 0x33, 0xFF).
    private static readonly MediaCompositionFrame BlueGrey = MediaCompositionFrame.Empty(MediaCompositionClearMode.ConfiguredColor, 0xFF336699);

    [Fact]
    public void TargetViews_AreReusedUntilTheTargetIsRecreated()
    {
        using NativeD3D11PresentationBridge bridge = CreateWarpBridge();
        var sink = new CountingFrameSink();
        using var handle = new MediaPresentationFrameSinkHandle(sink);
        MediaPresentationSurfaceDescriptor primary = Primary(handle.CreateSurfaceDescriptor(), 16, 8);
        D3D11TargetViewCache views = bridge.PresentationServices.Backends.ManagedFrameSink.TargetViews;

        Assert.True(bridge.AttachSurface(primary));
        Assert.True(bridge.PresentComposition(BlueGrey), bridge.LastPresentFailureReason);
        ID3D11RenderTargetView? first = views.CurrentView;
        Assert.NotNull(first);
        Assert.True(bridge.PresentComposition(BlueGrey), bridge.LastPresentFailureReason);
        Assert.Same(first, views.CurrentView);

        // A resize re-creates the texture: its view goes with it, and the next frame makes a new one.
        Assert.True(bridge.ResizeSurface(primary.WithSize(12, 6)));
        Assert.Null(views.CurrentView);
        Assert.True(bridge.PresentComposition(BlueGrey), bridge.LastPresentFailureReason);
        Assert.NotNull(views.CurrentView);
        Assert.NotSame(first, views.CurrentView);
        Assert.Equal((12, 6), (sink.Width, sink.Height));
    }

    [Fact]
    public void ManagedFrameSink_KeepsItsReadbackBufferThroughResizeChurn()
    {
        using NativeD3D11PresentationBridge bridge = CreateWarpBridge();
        var sink = new CountingFrameSink();
        using var handle = new MediaPresentationFrameSinkHandle(sink);
        MediaPresentationSurfaceDescriptor primary = Primary(handle.CreateSurfaceDescriptor(), 64, 48);
        var backend = bridge.PresentationServices.Backends.ManagedFrameSink;

        Assert.True(bridge.AttachSurface(primary));
        var capacity = backend.ReadbackBufferLength;
        Assert.Equal(64 * 48 * 4, capacity);

        foreach ((int width, int height) in new[] { (60, 44), (62, 46), (56, 40), (64, 48) })
        {
            Assert.True(bridge.ResizeSurface(primary.WithSize(width, height)));
            Assert.True(bridge.PresentComposition(BlueGrey), bridge.LastPresentFailureReason);
            Assert.Equal((width, height, width * 4), (sink.Width, sink.Height, sink.Stride));
            Assert.Equal(capacity, backend.ReadbackBufferLength);
        }

        Assert.True(bridge.ResizeSurface(primary.WithSize(96, 54)));
        Assert.Equal(96 * 54 * 4, backend.ReadbackBufferLength);
    }

    [Fact]
    public void PostProcessTargets_OfARemovedSecondaryAreReleased()
    {
        using NativeD3D11PresentationBridge bridge = CreateWarpBridge();
        using var primaryHandle = new MediaPresentationFrameSinkHandle(new CountingFrameSink());
        using var secondaryHandle = new MediaPresentationFrameSinkHandle(new CountingFrameSink());
        MediaPresentationSurfaceDescriptor secondary = new("singer", MediaPresentationSurfaceRole.Projector, secondaryHandle.CreateSurfaceDescriptor(), 8, 4);
        // A whole-composition transform draws each target through its own post-process texture
        // (the wash makes the frame use the shader pipeline).
        MediaCompositionFrame transformed = BlueGrey with
        {
            WashArgb = 0x40FF0000,
            BaseVisualTransform = new BaseVisualTransform { RedGain = 0.5f, IncludeOverlays = true },
        };

        Assert.True(bridge.AttachSurface(Primary(primaryHandle.CreateSurfaceDescriptor(), 16, 8)));
        Assert.True(bridge.AttachSurface(secondary));
        Assert.True(bridge.PresentComposition(transformed), bridge.LastPresentFailureReason);
        Assert.Equal(2, bridge.PresentationServices.MediaCompositor.PostProcessTargetCount);

        Assert.True(bridge.DetachSurface(secondary));
        Assert.True(bridge.PresentComposition(transformed), bridge.LastPresentFailureReason);
        Assert.Equal(1, bridge.PresentationServices.MediaCompositor.PostProcessTargetCount);
    }

    private static NativeD3D11PresentationBridge CreateWarpBridge()
    {
        var bridge = new NativeD3D11PresentationBridge(
            new MediaPlayerConfiguration(),
            new Direct3D11PresentationOptions { DeviceSelection = Direct3D11DeviceSelection.Warp });
        Assert.True(bridge.Initialize());
        return bridge;
    }

    private static MediaPresentationSurfaceDescriptor Primary(MediaNativeSurfaceDescriptor native, int width, int height) =>
        new(MediaPresentationSurfaceDescriptor.PrimarySurfaceId, MediaPresentationSurfaceRole.Primary, native, width, height);

    private sealed class CountingFrameSink : IMediaPresentationFrameSink
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Stride { get; private set; }

        public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride) =>
            (Width, Height, Stride) = (width, height, stride);
    }
}
