using System.Collections.Concurrent;
using FatzMedia.Video.Core.Graphics.Composition;
using FatzMedia.Video.Direct3D11;

namespace FatzMedia.Video.Tests;

/// <summary>
/// ENG-039 G4 and G6: resizing an offscreen primary (the WPF frame sink, Avalonia's shared
/// textures) announces no black frame, so the surface keeps its last picture until the next
/// composition, and the shared textures' re-export at each resize step is logged once. WARP, so
/// these run on GPU-less Windows CI; the drag-resize flicker itself is a manual smoke check.
/// </summary>
public sealed class D3D11OffscreenResizeTests
{
    // ConfiguredColor 0xFF336699 composes to BGRA (0x99, 0x66, 0x33, 0xFF).
    private static readonly MediaCompositionFrame BlueGrey = MediaCompositionFrame.Empty(MediaCompositionClearMode.ConfiguredColor, 0xFF336699);

    [Fact]
    public void ManagedFrameSink_ResizeAnnouncesNoFrameUntilTheNextComposition()
    {
        using NativeD3D11PresentationBridge bridge = D3D11MultiTargetTests.CreateWarpBridge();
        var sink = new D3D11MultiTargetTests.CopyingFrameSink();
        using var handle = new MediaPresentationFrameSinkHandle(sink);
        MediaPresentationSurfaceDescriptor primary = Primary(handle.CreateSurfaceDescriptor(), 16, 8);

        Assert.True(bridge.AttachSurface(primary));
        Assert.True(bridge.PresentComposition(BlueGrey), bridge.LastPresentFailureReason);
        var frames = sink.Frames;

        // Before ENG-039 each step pushed a black frame here.
        Assert.True(bridge.ResizeSurface(primary.WithSize(12, 6)));
        Assert.Equal(frames, sink.Frames);
        Assert.Equal((16, 8), (sink.Width, sink.Height));

        Assert.True(bridge.PresentComposition(BlueGrey), bridge.LastPresentFailureReason);
        Assert.Equal(frames + 1, sink.Frames);
        Assert.Equal((12, 6), (sink.Width, sink.Height));
        Assert.Equal(new byte[] { 0x99, 0x66, 0x33, 0xFF }, sink.Pixels[..4]);
    }

    [Fact]
    public void ManagedFrameSink_ANewSink_StillStartsBlack()
    {
        using NativeD3D11PresentationBridge bridge = D3D11MultiTargetTests.CreateWarpBridge();
        var first = new D3D11MultiTargetTests.CopyingFrameSink();
        var second = new D3D11MultiTargetTests.CopyingFrameSink();
        using var firstHandle = new MediaPresentationFrameSinkHandle(first);
        using var secondHandle = new MediaPresentationFrameSinkHandle(second);

        Assert.True(bridge.AttachSurface(Primary(firstHandle.CreateSurfaceDescriptor(), 16, 8)));
        Assert.True(bridge.AttachSurface(Primary(secondHandle.CreateSurfaceDescriptor(), 16, 8)));

        // Only a resize for the same sink skips the black frame; a new sink is cleared as before.
        Assert.Equal(1, second.Frames);
        Assert.Equal(new byte[] { 0, 0, 0, 0xFF }, second.Pixels[..4]);
    }

    [Fact]
    public void SharedTextures_ResizeAnnouncesNoBlackFrame()
    {
        using NativeD3D11PresentationBridge bridge = D3D11MultiTargetTests.CreateWarpBridge();
        var consumer = new CountingConsumer();
        using var handle = new MediaSharedGpuImageConsumerHandle(consumer);
        MediaPresentationSurfaceDescriptor primary = Primary(handle.CreateSurfaceDescriptor(), 16, 8);

        Assert.True(bridge.AttachSurface(primary), string.Join("; ", consumer.Unavailable));
        Assert.True(bridge.PresentComposition(BlueGrey), bridge.LastPresentFailureReason);
        var frames = consumer.Frames.Count;

        Assert.True(bridge.ResizeSurface(primary.WithSize(12, 6)), string.Join("; ", consumer.Unavailable));
        MediaSharedGpuImageSet resized = consumer.Sets.Last();
        Assert.Equal((12, 6), (resized.Width, resized.Height));
        Assert.Equal(frames, consumer.Frames.Count);

        // The next composition is the first frame of the new set.
        Assert.True(bridge.PresentComposition(BlueGrey), bridge.LastPresentFailureReason);
        Assert.Equal(frames + 1, consumer.Frames.Count);
        Assert.Equal(resized.Generation, consumer.Frames.Last().Generation);
        consumer.CompleteImports();
    }

    [Fact]
    public void SharedTextures_ResizeChurn_LogsOneExport()
    {
        var owner = new object();
        var exports = new ConcurrentQueue<string>();
        void Record(MediaDiagnosticInfo info)
        {
            if (ReferenceEquals(info.Owner, owner) && info.Message.StartsWith("Shared textures exported", StringComparison.Ordinal))
                exports.Enqueue(info.Message);
        }

        DiagnosticRouter.DiagnosticOccurred += Record;
        try
        {
            using DiagnosticRouter.OwnerScope scope = DiagnosticRouter.EnterOwner(owner);
            using NativeD3D11PresentationBridge bridge = D3D11MultiTargetTests.CreateWarpBridge();
            var consumer = new CountingConsumer();
            using var handle = new MediaSharedGpuImageConsumerHandle(consumer);
            MediaPresentationSurfaceDescriptor primary = Primary(handle.CreateSurfaceDescriptor(), 16, 8);

            Assert.True(bridge.AttachSurface(primary), string.Join("; ", consumer.Unavailable));
            foreach ((int width, int height) in new[] { (15, 8), (14, 7), (13, 7), (12, 6), (16, 8) })
                Assert.True(bridge.ResizeSurface(primary.WithSize(width, height)), string.Join("; ", consumer.Unavailable));

            Assert.Equal(6, consumer.Sets.Count);
            Assert.Single(exports);
            consumer.CompleteImports();
        }
        finally
        {
            DiagnosticRouter.DiagnosticOccurred -= Record;
        }
    }

    private static MediaPresentationSurfaceDescriptor Primary(MediaNativeSurfaceDescriptor native, int width, int height) =>
        new(MediaPresentationSurfaceDescriptor.PrimarySurfaceId, MediaPresentationSurfaceRole.Primary, native, width, height);

    /// <summary>Records sets and frames; releases nothing (a resize starts a new set and ring).</summary>
    private sealed class CountingConsumer : IMediaSharedGpuImageConsumer
    {
        public ConcurrentQueue<MediaSharedGpuImageSet> Sets { get; } = new();
        public ConcurrentQueue<MediaSharedGpuImageFrame> Frames { get; } = new();
        public ConcurrentQueue<string> Unavailable { get; } = new();

        public MediaSharedGpuImageRequest Request { get; } = new(
        [
            new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, MediaSharedGpuImageSyncKinds.KeyedMutex),
            new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.D3D11TextureGlobalSharedHandle, MediaSharedGpuImageSyncKinds.KeyedMutex)
        ]);

        public void OnImagesCreated(MediaSharedGpuImageSet images) => Sets.Enqueue(images);

        public void OnFrameReady(MediaSharedGpuImageFrame frame) => Frames.Enqueue(frame);

        public void OnUnavailable(string reason) => Unavailable.Enqueue(reason);

        /// <summary>Lets the producer close the NT handles of every set (nothing was imported).</summary>
        public void CompleteImports()
        {
            foreach (MediaSharedGpuImageSet set in Sets)
                set.CompleteImport(handlesConsumed: false);
        }
    }
}
