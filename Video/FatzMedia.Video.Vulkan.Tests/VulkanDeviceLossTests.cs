using System.Collections.Concurrent;
using FatzMedia.Video.Core.Graphics;
using FatzMedia.Video.Core.Graphics.Composition;
using Silk.NET.Vulkan;

namespace FatzMedia.Video.Vulkan.Tests;

/// <summary>
/// ENG-039: device-loss detection and recovery hand-off on the Vulkan renderer (lavapipe). A real
/// loss cannot be caused here, so the device is marked lost the way a failed call marks it.
/// </summary>
public sealed class VulkanDeviceLossTests
{
    private static readonly MediaCompositionFrame Wash = MediaCompositionFrame.Empty() with { WashArgb = 0x80FF8000 };

    [Fact]
    public void DeviceLost_RefusesFramesAndInitializeUntilCleanup_ThenRecreatesOnce()
    {
        List<VulkanOffscreenCompositor> created = [];
        using var backend = new VulkanRendererBackend(() =>
        {
            VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out _);
            if (compositor is not null)
                created.Add(compositor);
            return compositor;
        });
        if (!backend.Initialize())
            return;

        var sink = new CountingSink();
        using var handle = new MediaPresentationFrameSinkHandle(sink);
        Assert.True(backend.AttachSurface(SinkSurface(handle)));
        Assert.True(backend.PresentComposition(Wash));
        Assert.False(backend.ProbeDeviceLost(out _));

        created[0].MarkDeviceLost("test: the device is gone");

        Assert.True(backend.ProbeDeviceLost(out var reason));
        Assert.Equal("test: the device is gone", reason);
        Assert.True(backend.IsDeviceLostPending);
        Assert.False(backend.PresentComposition(Wash));
        Assert.False(backend.Initialize());
        Assert.Equal(0, backend.CreateVideoDecodeDeviceContext(out var failure));
        Assert.Contains("lost", failure);
        Assert.Single(created);

        // Core's recovery: one new device, and the surface (still attached) presents again.
        backend.CleanupResources();
        Assert.False(backend.ProbeDeviceLost(out _));
        Assert.True(backend.Initialize());
        Assert.Equal(2, created.Count);
        Assert.True(backend.PresentComposition(Wash));
        Assert.Equal(2, sink.Frames);
    }

    [Fact]
    public void Heartbeat_HealthyDevice_IsNotLost()
    {
        using VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out _);
        if (compositor is null)
            return;

        for (var i = 0; i < 10; i++)
        {
            Assert.False(compositor.ProbeDeviceLost(out var reason));
            Assert.Null(reason);
        }

        Assert.False(compositor.SharedDevice!.IsLost);
    }

    [Fact]
    public void Probe_AfterDispose_IsNotLost()
    {
        VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out _);
        if (compositor is null)
            return;

        compositor.Dispose();

        Assert.False(compositor.ProbeDeviceLost(out _));
    }

    [Fact]
    public void DecoderLossCheck_ReadsTheMarkAndTreatsAReleasedDeviceAsLost()
    {
        using var backend = new VulkanRendererBackend(() => null);
        VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out _);
        if (compositor is null)
            return;

        var device = new NativeVideoDeviceResource(MediaGraphicsBackendId.Vulkan, compositor.DeviceHandle);
        Assert.False(backend.IsVideoDecodeDeviceLost(device));
        compositor.MarkDeviceLost("marked");
        Assert.True(backend.IsVideoDecodeDeviceLost(device));
        compositor.Dispose();
        Assert.True(backend.IsVideoDecodeDeviceLost(device));
    }

    [Fact]
    public void ATimeout_MarksTheDeviceLost_ButOnlyTheDriverConfirmsIt()
    {
        // A slow GPU that misses the 2 s fence timeout is treated as lost, but its objects are still
        // waited for before they are destroyed: only a driver-confirmed loss skips that wait.
        VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out _);
        if (compositor is null)
            return;

        using (compositor)
        {
            VulkanSharedDevice shared = compositor.SharedDevice!;
            shared.MarkLost("frame did not complete within 2 s", confirmed: false);
            Assert.True(shared.IsLost);
            Assert.False(shared.IsLossConfirmed);

            shared.MarkLost();
            Assert.True(shared.IsLossConfirmed);
        }
    }

    [Fact]
    public void ClosingADeviceThatOnlyTimedOut_DoesNotWaitForTheWholeDevice()
    {
        // ENG-050: vkDeviceWaitIdle blocked recovery for 14 s after an NVIDIA GPU reset. A device
        // marked lost by a timeout waits only for the compositor's own submission, bounded.
        VulkanOffscreenCompositor? compositor = VulkanOffscreenCompositor.TryCreate(out _);
        if (compositor is null)
            return;

        compositor.SharedDevice!.MarkLost("frame did not complete within 2 s", confirmed: false);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        compositor.Dispose();
        Assert.True(watch.Elapsed < VulkanOffscreenCompositor.LostDeviceCloseWait, $"closing took {watch.Elapsed}");
    }

    [Fact]
    public void OutOfMemory_ThreeTimesInARow_CountsAsALostDevice()
    {
        VulkanOffscreenCompositor? compositor = null;
        using var backend = new VulkanRendererBackend(() => compositor = VulkanOffscreenCompositor.TryCreate(out _));
        if (!backend.Initialize())
            return;

        var outOfMemory = new VulkanException("vkAllocateMemory", Result.ErrorOutOfDeviceMemory);
        Assert.Equal(VulkanRendererBackend.PresentResult.Failed, backend.ReportRenderFailure(outOfMemory, compositor!));
        Assert.Equal(VulkanRendererBackend.PresentResult.Failed, backend.ReportRenderFailure(outOfMemory, compositor!));
        Assert.False(backend.IsDeviceLostPending);

        Assert.Equal(VulkanRendererBackend.PresentResult.DeviceLost, backend.ReportRenderFailure(outOfMemory, compositor!));
        Assert.True(backend.ProbeDeviceLost(out var reason));
        Assert.Contains("3 times in a row", reason);
    }

    [Fact]
    public void AnyFailure_ProbesTheDevice()
    {
        VulkanOffscreenCompositor? compositor = null;
        using var backend = new VulkanRendererBackend(() => compositor = VulkanOffscreenCompositor.TryCreate(out _));
        if (!backend.Initialize())
            return;

        Assert.Equal(VulkanRendererBackend.PresentResult.Failed, backend.ReportRenderFailure(new VulkanException("vkCreateImage", Result.ErrorUnknown), compositor!));
        compositor!.MarkDeviceLost("found by the probe");

        Assert.Equal(VulkanRendererBackend.PresentResult.DeviceLost, backend.ReportRenderFailure(new VulkanException("vkCreateImage", Result.ErrorUnknown), compositor));
        Assert.True(backend.ProbeDeviceLost(out var reason));
        Assert.Contains("found by the probe", reason);
    }

    [Fact]
    public void OutOfMemory_RightAfterTheConsumerLostItsContext_CountsAsALostDevice()
    {
        VulkanOffscreenCompositor? compositor = null;
        using var backend = new VulkanRendererBackend(() => compositor = VulkanOffscreenCompositor.TryCreate(out _));
        if (!backend.Initialize() || !compositor!.CanExportMemoryFd)
            return;

        var consumer = new RecordingConsumer();
        using var handle = new MediaSharedGpuImageConsumerHandle(consumer);
        Assert.True(backend.AttachSurface(SharedSurface(handle, 8, 8)));

        Assert.Single(consumer.Sets).ReportDeviceLost("PlatformGraphicsContextLostException");

        Assert.Equal(
            VulkanRendererBackend.PresentResult.DeviceLost,
            backend.ReportRenderFailure(new VulkanException("vkAllocateMemory", Result.ErrorOutOfDeviceMemory), compositor));
        Assert.True(backend.ProbeDeviceLost(out var reason));
        Assert.Contains("graphics context", reason);
        CloseHandles(consumer);
    }

    [Fact]
    public void HeldImages_PastTheStallTimeout_AreReplacedOncePerRelease()
    {
        VulkanOffscreenCompositor? compositor = null;
        using var backend = new VulkanRendererBackend(() => compositor = VulkanOffscreenCompositor.TryCreate(out _));
        if (!backend.Initialize() || !compositor!.CanExportMemoryFd)
            return;

        backend.SharedImageStallTimeout = TimeSpan.FromMilliseconds(50);
        var consumer = new RecordingConsumer();
        using var handle = new MediaSharedGpuImageConsumerHandle(consumer);
        Assert.True(backend.AttachSurface(SharedSurface(handle, 8, 8)));

        // Both images go to a compositor that never gives them back.
        Assert.True(backend.PresentComposition(Wash));
        Assert.True(backend.PresentComposition(Wash));
        Assert.True(backend.PresentComposition(Wash));
        Thread.Sleep(80);

        // Past the timeout the set is replaced (a failed frame), and the next frame uses the new one.
        Assert.False(backend.PresentComposition(Wash));
        Assert.True(consumer.Sets.First().IsRetired);
        Assert.True(backend.PresentComposition(Wash));
        Assert.Equal(2, consumer.Sets.Count);

        // The new set is held too, with no release since: no third set, just failed frames.
        Assert.True(backend.PresentComposition(Wash));
        Assert.True(backend.PresentComposition(Wash));
        Thread.Sleep(80);
        Assert.False(backend.PresentComposition(Wash));
        Assert.False(backend.PresentComposition(Wash));
        Assert.Equal(2, consumer.Sets.Count);

        // Once the compositor gives an image back, a later stall may replace the set again.
        MediaSharedGpuImageSet second = consumer.Sets.Last();
        second.ReleaseFrame(consumer.Frames.Last(), consumed: true);
        Assert.True(backend.PresentComposition(Wash));
        Assert.True(backend.PresentComposition(Wash));
        Thread.Sleep(80);
        Assert.False(backend.PresentComposition(Wash));
        Assert.True(backend.PresentComposition(Wash));
        Assert.Equal(3, consumer.Sets.Count);
        Assert.False(backend.IsDeviceLostPending);
        CloseHandles(consumer);
    }

    [Fact]
    public void ResizeChurn_LogsOneExport()
    {
        VulkanOffscreenCompositor? compositor = null;
        using var backend = new VulkanRendererBackend(() => compositor = VulkanOffscreenCompositor.TryCreate(out _));
        if (!backend.Initialize() || !compositor!.CanExportMemoryFd)
            return;

        var consumer = new RecordingConsumer();
        using var handle = new MediaSharedGpuImageConsumerHandle(consumer);
        Assert.True(backend.AttachSurface(SharedSurface(handle, 8, 8)));
        for (var size = 9; size < 19; size++)
            Assert.True(backend.ResizeSurface(SharedSurface(handle, size, size)));

        Assert.Equal(11, consumer.Sets.Count);
        Assert.Equal(1, backend.ExportsLogged);
        CloseHandles(consumer);
    }

    private static MediaPresentationSurfaceDescriptor SinkSurface(MediaPresentationFrameSinkHandle handle) => new(
        MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
        MediaPresentationSurfaceRole.Primary,
        handle.CreateSurfaceDescriptor(),
        8,
        6);

    private static MediaPresentationSurfaceDescriptor SharedSurface(MediaSharedGpuImageConsumerHandle handle, int width, int height) => new(
        MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
        MediaPresentationSurfaceRole.Primary,
        handle.CreateSurfaceDescriptor(),
        width,
        height);

    /// <summary>Nothing imported the descriptors: the producer closes them once the sets are retired.</summary>
    private static void CloseHandles(RecordingConsumer consumer)
    {
        foreach (MediaSharedGpuImageSet set in consumer.Sets)
            set.CompleteImport(handlesConsumed: false);
    }

    private sealed class CountingSink : IMediaPresentationFrameSink
    {
        public int Frames { get; private set; }

        public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride) => Frames++;
    }

    private sealed class RecordingConsumer : IMediaSharedGpuImageConsumer
    {
        public ConcurrentQueue<MediaSharedGpuImageSet> Sets { get; } = new();
        public ConcurrentQueue<MediaSharedGpuImageFrame> Frames { get; } = new();

        public MediaSharedGpuImageRequest Request { get; } = new(
            [new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.VulkanOpaqueFd, MediaSharedGpuImageSyncKinds.HostSynchronized)]);

        public void OnImagesCreated(MediaSharedGpuImageSet images) => Sets.Enqueue(images);

        public void OnFrameReady(MediaSharedGpuImageFrame frame) => Frames.Enqueue(frame);

        public void OnUnavailable(string reason)
        {
        }
    }
}
