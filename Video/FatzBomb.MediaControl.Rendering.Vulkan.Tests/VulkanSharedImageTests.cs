using System.Collections.Concurrent;
using FatzBomb.MediaControl.Core.Graphics.Composition;

namespace FatzBomb.MediaControl.Rendering.Vulkan.Tests;

/// <summary>
/// Stage 027A slice 2: exported shared images. Runs on any device with
/// <c>VK_KHR_external_memory_fd</c>; Mesa lavapipe has it (but not semaphore export), so CI covers
/// the export, the re-import on a second device, and the host-synchronized ring.
/// </summary>
public sealed class VulkanSharedImageTests
{
    // ConfiguredColor 0xFF336699 composes to BGRA (0x99, 0x66, 0x33, 0xFF); exported images are RGBA.
    private static readonly MediaCompositionFrame BlueGrey = MediaCompositionFrame.Empty(MediaCompositionClearMode.ConfiguredColor, 0xFF336699);
    private static readonly byte[] BlueGreyRgba = [0x33, 0x66, 0x99, 0xFF];

    [Fact]
    public void ExportedImage_ReimportedOnAnotherDevice_HoldsTheComposedFrame()
    {
        using VulkanOffscreenCompositor? producer = CreateExportingCompositor();
        if (producer is null)
            return;

        using VulkanOffscreenCompositor consumer = VulkanOffscreenCompositor.TryCreate(out _)!;
        VulkanExportedImageSet set = producer.CreateExportedImages(9, 5, 2, withSemaphores: false);
        Assert.Equal(2, set.Images.Count);
        Assert.All(set.Images, image => Assert.True(image.MemoryFd >= 0 && image.MemorySize > 0));

        producer.RenderToExport(VulkanCompositionPlanner.Plan(BlueGrey, 9, 5), set, 1, waitForConsumerRelease: false);
        // The import takes ownership of the descriptor.
        byte[] pixels = consumer.ImportAndReadBack(set.Images[1].MemoryFd, set.Images[1].MemorySize, 9, 5);

        AssertUniform(pixels, BlueGreyRgba);
        producer.DestroyExportedImages(set, closeDescriptors: false);
        VulkanOffscreenCompositor.CloseDescriptor(set.Images[0].MemoryFd);
    }

    [Fact]
    public void Backend_ExportsDoubleBufferedImagesAndWaitsForTheConsumer()
    {
        if (CreateExportingCompositor() is not { } probe)
            return;
        probe.Dispose();

        using var backend = new VulkanRendererBackend(() => VulkanOffscreenCompositor.TryCreate(out _));
        var consumer = new RecordingConsumer(new MediaSharedGpuImageRequest(
            [new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.VulkanOpaqueFd, MediaSharedGpuImageSyncKinds.Semaphores | MediaSharedGpuImageSyncKinds.HostSynchronized)],
            [MediaSharedGpuSemaphoreHandleKind.VulkanOpaqueFd]));
        using var handle = new MediaSharedGpuImageConsumerHandle(consumer);
        MediaPresentationSurfaceDescriptor surface = Surface(handle, 9, 5);

        Assert.True(backend.AttachSurface(surface));
        MediaSharedGpuImageSet set = Assert.Single(consumer.Sets);
        Assert.Equal((9, 5, MediaSharedGpuImageFormat.Rgba8Unorm, 2), (set.Width, set.Height, set.Format, set.Buffers.Count));
        Assert.Equal(MediaSharedGpuImageHandleKind.VulkanOpaqueFd, set.HandleKind);
        Assert.Equal((int)VulkanOffscreenCompositor.ExportLayout, set.VulkanImageLayout);

        Assert.True(backend.PresentComposition(BlueGrey));
        Assert.True(backend.PresentComposition(BlueGrey));
        MediaSharedGpuImageFrame[] frames = [.. consumer.Frames];
        Assert.Equal([0, 1], frames.Select(static frame => frame.BufferIndex));

        // Both images are with the compositor: the next frame waits, then is dropped (not failed).
        Assert.True(backend.PresentComposition(BlueGrey));
        Assert.Equal(2, consumer.Frames.Count);

        set.ReleaseFrame(frames[0], consumed: true);
        Assert.True(backend.PresentComposition(BlueGrey));
        Assert.Equal(0, consumer.Frames.Last().BufferIndex);

        // What the compositor would import shows the frame.
        using (VulkanOffscreenCompositor importer = VulkanOffscreenCompositor.TryCreate(out _)!)
        {
            AssertUniform(importer.ImportAndReadBack((int)set.Buffers[0].ImageHandle, set.Buffers[0].MemorySize, 9, 5), BlueGreyRgba);
            VulkanOffscreenCompositor.CloseDescriptor((int)set.Buffers[1].ImageHandle);
            set.CompleteImport(handlesConsumed: true);
        }

        // A resize replaces the set; the old one is retired and released.
        Assert.True(backend.ResizeSurface(surface.WithSize(12, 6)));
        Assert.Equal(2, consumer.Sets.Count);
        Assert.True(set.IsRetired);
        Assert.Equal((12, 6), (consumer.Sets.Last().Width, consumer.Sets.Last().Height));
        Assert.True(consumer.Sets.Last().Generation > set.Generation);

        Assert.True(backend.DetachSurface(surface));
        Assert.True(consumer.Sets.Last().IsRetired);
        consumer.Sets.Last().CompleteImport(handlesConsumed: false);
        Assert.Empty(consumer.Unavailable);
    }

    [Fact]
    public void Backend_DeclinesAConsumerThatImportsNoDescriptorKind()
    {
        if (!VulkanPresentationCoordinatorFactory.IsSupported(out _))
            return;

        using var backend = new VulkanRendererBackend(() => VulkanOffscreenCompositor.TryCreate(out _));
        var consumer = new RecordingConsumer(new MediaSharedGpuImageRequest(
            [new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, MediaSharedGpuImageSyncKinds.KeyedMutex)]));
        using var handle = new MediaSharedGpuImageConsumerHandle(consumer);

        Assert.False(backend.AttachSurface(Surface(handle, 8, 8)));
        Assert.Empty(consumer.Sets);
        Assert.Single(consumer.Unavailable);
    }

    [Fact]
    public void Backend_DeclinesAConsumerOnAnotherGpu()
    {
        if (CreateExportingCompositor() is not { } probe)
            return;
        probe.Dispose();

        using var backend = new VulkanRendererBackend(() => VulkanOffscreenCompositor.TryCreate(out _));
        var consumer = new RecordingConsumer(new MediaSharedGpuImageRequest(
            [new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.VulkanOpaqueFd, MediaSharedGpuImageSyncKinds.HostSynchronized)],
            deviceUuid: Enumerable.Repeat((byte)0xEE, 16).ToArray()));
        using var handle = new MediaSharedGpuImageConsumerHandle(consumer);

        Assert.False(backend.AttachSurface(Surface(handle, 8, 8)));
        Assert.Contains("UUID", Assert.Single(consumer.Unavailable));
    }

    [Fact]
    public void ExportCapabilities_OfferHostSynchronizationAndSemaphoresWhenExportable()
    {
        Assert.Empty(VulkanRendererBackend.GetExportCapabilities(false, true).Images);
        (MediaSharedGpuImageCapability[] images, MediaSharedGpuSemaphoreHandleKind[] semaphores) = VulkanRendererBackend.GetExportCapabilities(true, false);
        Assert.Equal([new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.VulkanOpaqueFd, MediaSharedGpuImageSyncKinds.HostSynchronized)], images);
        Assert.Empty(semaphores);
        (images, semaphores) = VulkanRendererBackend.GetExportCapabilities(true, true);
        Assert.Equal(MediaSharedGpuImageSyncKinds.HostSynchronized | MediaSharedGpuImageSyncKinds.Semaphores, images[0].SyncKinds);
        Assert.Equal([MediaSharedGpuSemaphoreHandleKind.VulkanOpaqueFd], semaphores);
    }

    [Fact]
    public void WindowsExport_MeetsAngleWithKeyedMutexTextures_AndAvaloniasVulkanBackendWithNtHandles()
    {
        // Stage 045A slice 3: what a Windows device with every path offers.
        (MediaSharedGpuImageCapability[] images, MediaSharedGpuSemaphoreHandleKind[] semaphores) =
            VulkanRendererBackend.GetExportCapabilities(new VulkanExportSupport(false, false, MemoryWin32: true, SemaphoreWin32: true, D3D11KeyedMutex: true));
        Assert.Equal(
            [
                new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, MediaSharedGpuImageSyncKinds.KeyedMutex),
                new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.VulkanOpaqueNtHandle, MediaSharedGpuImageSyncKinds.HostSynchronized | MediaSharedGpuImageSyncKinds.Semaphores),
            ],
            images);
        Assert.Equal([MediaSharedGpuSemaphoreHandleKind.VulkanOpaqueNtHandle], semaphores);

        // Avalonia's default compositor (ANGLE on Direct3D11) imports DXGI textures with a keyed mutex.
        var angle = new MediaSharedGpuImageRequest(
            [
                new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, MediaSharedGpuImageSyncKinds.KeyedMutex),
                new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.D3D11TextureGlobalSharedHandle, MediaSharedGpuImageSyncKinds.KeyedMutex),
            ]);
        Assert.True(angle.TrySelect(images, semaphores, out MediaSharedGpuImageSelection selection, out var reason), reason);
        Assert.Equal((MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, MediaSharedGpuImageSyncKinds.KeyedMutex), (selection.HandleKind, selection.SyncKind));

        // Avalonia's Vulkan backend takes opaque NT handles with exported semaphores.
        var vulkan = new MediaSharedGpuImageRequest(
            [new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.VulkanOpaqueNtHandle, MediaSharedGpuImageSyncKinds.Semaphores | MediaSharedGpuImageSyncKinds.HostSynchronized)],
            [MediaSharedGpuSemaphoreHandleKind.VulkanOpaqueNtHandle]);
        Assert.True(vulkan.TrySelect(images, semaphores, out selection, out reason), reason);
        Assert.Equal(
            (MediaSharedGpuImageHandleKind.VulkanOpaqueNtHandle, MediaSharedGpuImageSyncKinds.Semaphores, MediaSharedGpuSemaphoreHandleKind.VulkanOpaqueNtHandle),
            (selection.HandleKind, selection.SyncKind, selection.SemaphoreHandleKind));

        // Without the keyed-mutex path ANGLE has nothing in common, and is told so.
        (images, semaphores) = VulkanRendererBackend.GetExportCapabilities(new VulkanExportSupport(false, false, MemoryWin32: true, SemaphoreWin32: false, D3D11KeyedMutex: false));
        Assert.False(angle.TrySelect(images, semaphores, out _, out reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void ExportBuffers_CarryTheHandlesOfTheirKind()
    {
        var image = new VulkanExportedImage(default, default, 4096, 7, default, 8, default, 9)
        {
            MemoryNtHandle = 0x70,
            RenderCompleteNtHandle = 0x80,
            ConsumerReleaseNtHandle = 0x90,
        };
        Assert.Equal(new MediaSharedGpuImageBuffer(7, 4096, 0, 8, 9), VulkanRendererBackend.ToBuffer(image, MediaSharedGpuImageHandleKind.VulkanOpaqueFd));
        Assert.Equal(new MediaSharedGpuImageBuffer(0x70, 4096, 0, 0x80, 0x90), VulkanRendererBackend.ToBuffer(image, MediaSharedGpuImageHandleKind.VulkanOpaqueNtHandle));
        Assert.Equal(new MediaSharedGpuImageBuffer(0x70, 4096, 0, 0x80, 0x90), VulkanRendererBackend.ToBuffer(image, MediaSharedGpuImageHandleKind.D3D11TextureNtHandle));
    }

    /// <summary>A compositor that can export memory descriptors; on Linux (lavapipe in CI) it must.</summary>
    private static VulkanOffscreenCompositor? CreateExportingCompositor()
    {
        VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out _);
        if (compositor is null)
            return null;

        if (OperatingSystem.IsLinux())
            Assert.True(compositor.CanExportMemoryFd, compositor.ExportUnavailableReason);

        if (compositor.CanExportMemoryFd)
            return compositor;

        compositor.Dispose();
        return null;
    }

    private static MediaPresentationSurfaceDescriptor Surface(MediaSharedGpuImageConsumerHandle handle, int width, int height) => new(
        MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
        MediaPresentationSurfaceRole.Primary,
        handle.CreateSurfaceDescriptor(),
        width,
        height);

    private static void AssertUniform(byte[] pixels, byte[] expected)
    {
        for (var offset = 0; offset < pixels.Length; offset += 4)
            Assert.Equal(expected, pixels[offset..(offset + 4)]);
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
