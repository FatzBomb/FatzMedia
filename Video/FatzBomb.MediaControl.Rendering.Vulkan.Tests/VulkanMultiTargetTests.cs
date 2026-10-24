using System.Collections.Concurrent;
using FatzBomb.MediaControl.Core.Graphics.Composition;

namespace FatzBomb.MediaControl.Rendering.Vulkan.Tests;

/// <summary>
/// Stage 027A slice 5: a primary and secondary screens on one Vulkan backend. Every composition is
/// rendered once per attached surface at that surface's size, through a frame sink or an exported
/// image set of its own. Runs on lavapipe in CI.
/// </summary>
public sealed class VulkanMultiTargetTests
{
    private const string SecondaryId = "secondary";

    // ConfiguredColor 0xFF336699 composes to BGRA (0x99, 0x66, 0x33, 0xFF); exported images are RGBA.
    private static readonly MediaCompositionFrame BlueGrey = MediaCompositionFrame.Empty(MediaCompositionClearMode.ConfiguredColor, 0xFF336699);
    private static readonly byte[] BlueGreyBgra = [0x99, 0x66, 0x33, 0xFF];
    private static readonly byte[] BlueGreyRgba = [0x33, 0x66, 0x99, 0xFF];

    [Fact]
    public void PrimaryAndSecondaryFrameSinks_EachReceiveTheFrameAtTheirOwnSize()
    {
        if (!VulkanPresentationCoordinatorFactory.IsSupported(out _))
            return;

        var primarySink = new CopyingFrameSink();
        var secondarySink = new CopyingFrameSink();
        using var primaryHandle = new MediaPresentationFrameSinkHandle(primarySink);
        using var secondaryHandle = new MediaPresentationFrameSinkHandle(secondarySink);
        using var backend = new VulkanRendererBackend(() => VulkanOffscreenCompositor.TryCreate(out _));
        var presented = 0;
        backend.FramePresented += (_, _) => presented++;
        MediaPresentationSurfaceDescriptor primary = Primary(primaryHandle.CreateSurfaceDescriptor(), 8, 6);
        MediaPresentationSurfaceDescriptor secondary = Secondary(secondaryHandle.CreateSurfaceDescriptor(), 20, 4);

        Assert.True(backend.AttachSurface(primary));
        Assert.True(backend.AttachSurface(secondary));
        Assert.True(backend.PresentComposition(BlueGrey));

        Assert.Equal((8, 6, 32), (primarySink.Width, primarySink.Height, primarySink.Stride));
        Assert.Equal((20, 4, 80), (secondarySink.Width, secondarySink.Height, secondarySink.Stride));
        AssertUniform(primarySink.Pixels, BlueGreyBgra);
        AssertUniform(secondarySink.Pixels, BlueGreyBgra);
        Assert.Equal(1, presented);

        // Resizing one leaves the other alone.
        Assert.True(backend.ResizeSurface(secondary.WithSize(3, 9)));
        Assert.True(backend.PresentComposition(BlueGrey));
        Assert.Equal((8, 6), (primarySink.Width, primarySink.Height));
        Assert.Equal((3, 9), (secondarySink.Width, secondarySink.Height));

        // Detaching the secondary keeps the primary presenting; a secondary on its own presents too.
        Assert.True(backend.DetachSurface(secondary));
        Assert.True(backend.PresentComposition(BlueGrey));
        Assert.Equal(3, primarySink.Frames);
        Assert.Equal(2, secondarySink.Frames);
        Assert.True(backend.DetachSurface(primary));
        Assert.True(backend.AttachSurface(secondary));
        Assert.True(backend.PresentComposition(BlueGrey));
        Assert.Equal(3, secondarySink.Frames);
    }

    [Fact]
    public void TargetFilter_PresentsOnlyTheNamedSurfaces()
    {
        if (!VulkanPresentationCoordinatorFactory.IsSupported(out _))
            return;

        var primarySink = new CopyingFrameSink();
        var secondarySink = new CopyingFrameSink();
        using var primaryHandle = new MediaPresentationFrameSinkHandle(primarySink);
        using var secondaryHandle = new MediaPresentationFrameSinkHandle(secondarySink);
        using var backend = new VulkanRendererBackend(() => VulkanOffscreenCompositor.TryCreate(out _));
        Assert.True(backend.AttachSurface(Primary(primaryHandle.CreateSurfaceDescriptor(), 4, 4)));
        Assert.True(backend.AttachSurface(Secondary(secondaryHandle.CreateSurfaceDescriptor(), 6, 2)));

        // Display-locked video on two monitors (Stage 017A slice 5) composes per display group.
        Assert.True(backend.PresentComposition(BlueGrey with { Targets = new MediaCompositionTargetFilter(false, [SecondaryId]) }));
        Assert.Equal((0, 1), (primarySink.Frames, secondarySink.Frames));
        Assert.True(backend.PresentComposition(BlueGrey with { Targets = new MediaCompositionTargetFilter(true, []) }));
        Assert.Equal((1, 1), (primarySink.Frames, secondarySink.Frames));

        // A filter naming only a surface that is gone is nothing to do, not a failure.
        Assert.True(backend.PresentComposition(BlueGrey with { Targets = new MediaCompositionTargetFilter(false, ["gone"]) }));
        Assert.Equal((1, 1), (primarySink.Frames, secondarySink.Frames));
    }

    [Fact]
    public void Compositor_KeepsOneRenderTargetPerSizeUpToTheLimit()
    {
        using VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out _);
        if (compositor is null)
            return;

        for (var frame = 0; frame < 3; frame++)
        {
            Assert.Equal(64 * 48 * 4, compositor.Render(VulkanCompositionPlanner.Plan(BlueGrey, 64, 48)).Length);
            ReadOnlySpan<byte> small = compositor.Render(VulkanCompositionPlanner.Plan(BlueGrey, 5, 3));
            Assert.Equal(5 * 3 * 4, small.Length);
            AssertUniform(small.ToArray(), BlueGreyBgra);
        }

        Assert.Equal(2, compositor.RenderTargetCount);
        for (var size = 1; size <= VulkanOffscreenCompositor.MaxRenderTargets + 2; size++)
            _ = compositor.Render(VulkanCompositionPlanner.Plan(BlueGrey, size, size));

        Assert.Equal(VulkanOffscreenCompositor.MaxRenderTargets, compositor.RenderTargetCount);
    }

    [Fact]
    public void SecondarySharedImages_AreExportedAtTheSecondarySizeBesideAPrimaryFrameSink()
    {
        if (!CanExport())
            return;

        var primarySink = new CopyingFrameSink();
        using var primaryHandle = new MediaPresentationFrameSinkHandle(primarySink);
        var consumer = new RecordingConsumer(HostSynchronizedRequest());
        using var consumerHandle = new MediaSharedGpuImageConsumerHandle(consumer);
        using var backend = new VulkanRendererBackend(() => VulkanOffscreenCompositor.TryCreate(out _));
        MediaPresentationSurfaceDescriptor secondary = Secondary(consumerHandle.CreateSurfaceDescriptor(), 9, 5);

        Assert.True(backend.AttachSurface(Primary(primaryHandle.CreateSurfaceDescriptor(), 4, 4)));
        Assert.True(backend.AttachSurface(secondary));
        MediaSharedGpuImageSet set = Assert.Single(consumer.Sets);
        Assert.Equal((9, 5), (set.Width, set.Height));

        Assert.True(backend.PresentComposition(BlueGrey));
        Assert.Equal(1, primarySink.Frames);
        MediaSharedGpuImageFrame frame = Assert.Single(consumer.Frames);
        Assert.Equal(set.Generation, frame.Generation);
        using (VulkanOffscreenCompositor importer = VulkanOffscreenCompositor.TryCreate(out _)!)
        {
            AssertUniform(importer.ImportAndReadBack((int)set.Buffers[frame.BufferIndex].ImageHandle, set.Buffers[frame.BufferIndex].MemorySize, 9, 5), BlueGreyRgba);
            VulkanOffscreenCompositor.CloseDescriptor((int)set.Buffers[1 - frame.BufferIndex].ImageHandle);
            set.CompleteImport(handlesConsumed: true);
        }

        // Resizing the secondary exports a new set for it alone.
        Assert.True(backend.ResizeSurface(secondary.WithSize(12, 6)));
        Assert.True(set.IsRetired);
        Assert.Equal((12, 6), (consumer.Sets.Last().Width, consumer.Sets.Last().Height));

        Assert.True(backend.DetachSurface(secondary));
        Assert.True(consumer.Sets.Last().IsRetired);
        consumer.Sets.Last().CompleteImport(handlesConsumed: false);
        Assert.True(backend.PresentComposition(BlueGrey));
        Assert.Equal(2, primarySink.Frames);
        Assert.Single(consumer.Frames);
        Assert.Empty(consumer.Unavailable);
    }

    [Fact]
    public void PrimaryAndSecondarySharedImages_HaveSeparateSetsAndRings()
    {
        if (!CanExport())
            return;

        var primary = new RecordingConsumer(HostSynchronizedRequest());
        var secondary = new RecordingConsumer(HostSynchronizedRequest());
        using var primaryHandle = new MediaSharedGpuImageConsumerHandle(primary);
        using var secondaryHandle = new MediaSharedGpuImageConsumerHandle(secondary);
        using var backend = new VulkanRendererBackend(() => VulkanOffscreenCompositor.TryCreate(out _));

        Assert.True(backend.AttachSurface(Primary(primaryHandle.CreateSurfaceDescriptor(), 8, 8)));
        Assert.True(backend.AttachSurface(Secondary(secondaryHandle.CreateSurfaceDescriptor(), 16, 4)));
        MediaSharedGpuImageSet primarySet = Assert.Single(primary.Sets);
        MediaSharedGpuImageSet secondarySet = Assert.Single(secondary.Sets);
        Assert.NotEqual(primarySet.Generation, secondarySet.Generation);
        Assert.Equal((16, 4), (secondarySet.Width, secondarySet.Height));

        Assert.True(backend.PresentComposition(BlueGrey));
        Assert.True(backend.PresentComposition(BlueGrey));
        Assert.Equal([0, 1], primary.Frames.Select(static frame => frame.BufferIndex));
        Assert.Equal([0, 1], secondary.Frames.Select(static frame => frame.BufferIndex));

        // The secondary's compositor gives an image back; the primary's holds both. The primary's
        // frame is dropped (not a failure) while the secondary keeps presenting.
        secondarySet.ReleaseFrame(secondary.Frames.First(), consumed: true);
        Assert.True(backend.PresentComposition(BlueGrey));
        Assert.Equal(2, primary.Frames.Count);
        Assert.Equal(3, secondary.Frames.Count);

        foreach (MediaSharedGpuImageSet set in new[] { primarySet, secondarySet })
        {
            backend.Dispose();
            Assert.True(set.IsRetired);
            set.CompleteImport(handlesConsumed: false);
        }
    }

    private static bool CanExport()
    {
        using VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out _);
        if (compositor is null)
            return false;

        if (OperatingSystem.IsLinux())
            Assert.True(compositor.CanExportMemoryFd, compositor.ExportUnavailableReason);

        return compositor.CanExportMemoryFd;
    }

    private static MediaSharedGpuImageRequest HostSynchronizedRequest() => new(
        [new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.VulkanOpaqueFd, MediaSharedGpuImageSyncKinds.HostSynchronized)]);

    private static MediaPresentationSurfaceDescriptor Primary(MediaNativeSurfaceDescriptor native, int width, int height) =>
        new(MediaPresentationSurfaceDescriptor.PrimarySurfaceId, MediaPresentationSurfaceRole.Primary, native, width, height);

    private static MediaPresentationSurfaceDescriptor Secondary(MediaNativeSurfaceDescriptor native, int width, int height) =>
        new(SecondaryId, MediaPresentationSurfaceRole.Projector, native, width, height);

    private static void AssertUniform(byte[] pixels, byte[] expected)
    {
        Assert.NotEmpty(pixels);
        for (var offset = 0; offset < pixels.Length; offset += 4)
            Assert.Equal(expected, pixels[offset..(offset + 4)]);
    }

    private sealed class CopyingFrameSink : IMediaPresentationFrameSink
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Stride { get; private set; }
        public int Frames { get; private set; }
        public byte[] Pixels { get; private set; } = [];

        public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
        {
            (Width, Height, Stride) = (width, height, stride);
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
