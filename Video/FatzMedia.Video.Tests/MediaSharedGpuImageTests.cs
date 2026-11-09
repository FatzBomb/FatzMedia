namespace FatzMedia.Video.Tests;

public sealed class MediaSharedGpuImageRingTests
{
    [Fact]
    public void FirstWrites_AlternateImagesWithTheProducerKeyAndNoPendingWait()
    {
        using var ring = new MediaSharedGpuImageRing(2);
        ring.Reset(7);

        Assert.True(ring.TryBeginWrite(TimeSpan.Zero, out MediaSharedGpuImageWrite first));
        Assert.Equal(new MediaSharedGpuImageWrite(0, MediaSharedGpuImageRing.ProducerKey, false), first);
        MediaSharedGpuImageFrame frame0 = ring.CompleteWrite(first.BufferIndex);

        Assert.True(ring.TryBeginWrite(TimeSpan.Zero, out MediaSharedGpuImageWrite second));
        Assert.Equal(new MediaSharedGpuImageWrite(1, MediaSharedGpuImageRing.ProducerKey, false), second);
        MediaSharedGpuImageFrame frame1 = ring.CompleteWrite(second.BufferIndex);

        Assert.Equal(new MediaSharedGpuImageFrame(7, 0, 1, MediaSharedGpuImageRing.ConsumerKey, MediaSharedGpuImageRing.ProducerKey), frame0);
        Assert.Equal(new MediaSharedGpuImageFrame(7, 1, 2, MediaSharedGpuImageRing.ConsumerKey, MediaSharedGpuImageRing.ProducerKey), frame1);
        Assert.Equal(2, ring.InFlightCount);
    }

    [Fact]
    public void AllImagesInFlight_NoWriteUntilTheConsumerReleasesOne()
    {
        using var ring = new MediaSharedGpuImageRing(2);
        ring.Reset(1);
        MediaSharedGpuImageFrame frame0 = Present(ring);
        _ = Present(ring);

        Assert.False(ring.TryBeginWrite(TimeSpan.Zero, out _));

        Assert.True(ring.Release(frame0, consumed: true));
        Assert.True(ring.TryBeginWrite(TimeSpan.Zero, out MediaSharedGpuImageWrite next));
        // The compositor released the mutex with the producer key and signalled its semaphore.
        Assert.Equal(new MediaSharedGpuImageWrite(0, MediaSharedGpuImageRing.ProducerKey, true), next);
    }

    [Fact]
    public void UnusedFrame_IsReacquiredWithTheConsumerKeyAndNoSemaphoreWait()
    {
        using var ring = new MediaSharedGpuImageRing(1);
        ring.Reset(1);
        MediaSharedGpuImageFrame frame = Present(ring);

        Assert.True(ring.Release(frame, consumed: false));
        Assert.True(ring.TryBeginWrite(TimeSpan.Zero, out MediaSharedGpuImageWrite next));
        Assert.Equal(new MediaSharedGpuImageWrite(0, MediaSharedGpuImageRing.ConsumerKey, false), next);
    }

    [Fact]
    public void PendingSemaphoreWait_IsClearedByTheWriteThatWaitedOnIt()
    {
        using var ring = new MediaSharedGpuImageRing(1);
        ring.Reset(1);
        Assert.True(ring.Release(Present(ring), consumed: true));
        Assert.True(ring.TryBeginWrite(TimeSpan.Zero, out MediaSharedGpuImageWrite waited));
        Assert.True(waited.WaitForConsumerRelease);
        MediaSharedGpuImageFrame frame = ring.CompleteWrite(waited.BufferIndex);

        Assert.True(ring.Release(frame, consumed: false));
        Assert.True(ring.TryBeginWrite(TimeSpan.Zero, out MediaSharedGpuImageWrite next));
        Assert.False(next.WaitForConsumerRelease);
    }

    [Fact]
    public void FreeImages_TheLeastRecentlyPresentedIsWrittenFirst()
    {
        using var ring = new MediaSharedGpuImageRing(2);
        ring.Reset(1);
        MediaSharedGpuImageFrame frame0 = Present(ring);
        MediaSharedGpuImageFrame frame1 = Present(ring);

        // Released out of order: image 0 is still the older one.
        Assert.True(ring.Release(frame1, consumed: true));
        Assert.True(ring.Release(frame0, consumed: true));

        Assert.True(ring.TryBeginWrite(TimeSpan.Zero, out MediaSharedGpuImageWrite next));
        Assert.Equal(0, next.BufferIndex);
    }

    [Fact]
    public void Releases_OfAnOlderGenerationOrTwice_AreIgnored()
    {
        using var ring = new MediaSharedGpuImageRing(2);
        ring.Reset(1);
        MediaSharedGpuImageFrame old = Present(ring);
        ring.Reset(2);
        Assert.Equal(2, ring.FreeCount);

        Assert.False(ring.Release(old, consumed: true));
        MediaSharedGpuImageFrame current = Present(ring);
        Assert.Equal(2, current.Generation);
        Assert.True(ring.Release(current, consumed: true));
        Assert.False(ring.Release(current, consumed: true));
        Assert.False(ring.Release(2, 5, consumed: true));
    }

    [Fact]
    public void AbortedWrite_FreesTheImageAndKeepsItsKey()
    {
        using var ring = new MediaSharedGpuImageRing(1);
        ring.Reset(1);
        Assert.True(ring.Release(Present(ring), consumed: false));
        Assert.True(ring.TryBeginWrite(TimeSpan.Zero, out MediaSharedGpuImageWrite write));
        ring.AbortWrite(write.BufferIndex);

        Assert.True(ring.TryBeginWrite(TimeSpan.Zero, out MediaSharedGpuImageWrite retry));
        Assert.Equal(MediaSharedGpuImageRing.ConsumerKey, retry.AcquireKey);
        _ = ring.CompleteWrite(retry.BufferIndex);
        Assert.Throws<InvalidOperationException>(() => ring.CompleteWrite(retry.BufferIndex));
    }

    [Fact]
    public async Task BlockedWrite_ProceedsWhenTheConsumerReleasesFromAnotherThread()
    {
        using var ring = new MediaSharedGpuImageRing(1);
        ring.Reset(1);
        MediaSharedGpuImageFrame frame = Present(ring);

        Task<bool> waiting = Task.Run(() => ring.TryBeginWrite(TimeSpan.FromSeconds(10), out _));
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);
        Assert.True(ring.Release(frame, consumed: true));

        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void BlockedWrite_TimesOut()
    {
        using var ring = new MediaSharedGpuImageRing(1);
        ring.Reset(1);
        _ = Present(ring);

        Assert.False(ring.TryBeginWrite(TimeSpan.FromMilliseconds(20), out _));
    }

    private static MediaSharedGpuImageFrame Present(MediaSharedGpuImageRing ring)
    {
        Assert.True(ring.TryBeginWrite(TimeSpan.Zero, out MediaSharedGpuImageWrite write));
        return ring.CompleteWrite(write.BufferIndex);
    }
}

public sealed class MediaSharedGpuImageRequestTests
{
    private static readonly MediaSharedGpuImageCapability NtKeyedMutex = new(MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, MediaSharedGpuImageSyncKinds.KeyedMutex);
    private static readonly MediaSharedGpuImageCapability GlobalKeyedMutex = new(MediaSharedGpuImageHandleKind.D3D11TextureGlobalSharedHandle, MediaSharedGpuImageSyncKinds.KeyedMutex);

    [Fact]
    public void TrySelect_FollowsTheConsumerPreferenceOrder()
    {
        var request = new MediaSharedGpuImageRequest([NtKeyedMutex, GlobalKeyedMutex]);

        Assert.True(request.TrySelect([GlobalKeyedMutex, NtKeyedMutex], [], out MediaSharedGpuImageSelection selection, out _));
        Assert.Equal(new MediaSharedGpuImageSelection(MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, MediaSharedGpuImageSyncKinds.KeyedMutex, MediaSharedGpuSemaphoreHandleKind.None), selection);
    }

    [Fact]
    public void TrySelect_SemaphoresNeedACommonSemaphoreHandleKind()
    {
        var consumer = new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.VulkanOpaqueFd, MediaSharedGpuImageSyncKinds.Semaphores);
        var request = new MediaSharedGpuImageRequest([consumer], [MediaSharedGpuSemaphoreHandleKind.VulkanOpaqueFd]);
        var producer = new MediaSharedGpuImageCapability(
            MediaSharedGpuImageHandleKind.VulkanOpaqueFd,
            MediaSharedGpuImageSyncKinds.Semaphores | MediaSharedGpuImageSyncKinds.HostSynchronized);

        Assert.True(request.TrySelect([producer], [MediaSharedGpuSemaphoreHandleKind.VulkanOpaqueFd], out MediaSharedGpuImageSelection selection, out _));
        Assert.Equal(MediaSharedGpuImageSyncKinds.Semaphores, selection.SyncKind);
        Assert.Equal(MediaSharedGpuSemaphoreHandleKind.VulkanOpaqueFd, selection.SemaphoreHandleKind);

        Assert.False(request.TrySelect([producer], [], out _, out var reason));
        Assert.Contains("no common synchronization", reason);
    }

    [Fact]
    public void TrySelect_FallsBackToHostSynchronizationWhenBothOfferIt()
    {
        var request = new MediaSharedGpuImageRequest(
            [new(MediaSharedGpuImageHandleKind.VulkanOpaqueFd, MediaSharedGpuImageSyncKinds.Semaphores | MediaSharedGpuImageSyncKinds.HostSynchronized)],
            [MediaSharedGpuSemaphoreHandleKind.VulkanOpaqueFd]);

        Assert.True(request.TrySelect(
            [new(MediaSharedGpuImageHandleKind.VulkanOpaqueFd, MediaSharedGpuImageSyncKinds.Semaphores | MediaSharedGpuImageSyncKinds.HostSynchronized)],
            [],
            out MediaSharedGpuImageSelection selection,
            out _));
        Assert.Equal(MediaSharedGpuImageSyncKinds.HostSynchronized, selection.SyncKind);
    }

    [Fact]
    public void TrySelect_NoCommonHandleKind_SaysSo()
    {
        var request = new MediaSharedGpuImageRequest([NtKeyedMutex]);

        Assert.False(request.TrySelect(
            [new(MediaSharedGpuImageHandleKind.VulkanOpaqueFd, MediaSharedGpuImageSyncKinds.Semaphores)],
            [MediaSharedGpuSemaphoreHandleKind.VulkanOpaqueFd],
            out _,
            out var reason));
        Assert.Contains("no common image handle kind", reason);
        Assert.False(new MediaSharedGpuImageRequest([]).IsValid);
    }

    [Fact]
    public void MatchesDevice_ComparesOnlyIdentitiesBothSidesKnow()
    {
        byte[] luid = [1, 2, 3, 4, 5, 6, 7, 8];
        var request = new MediaSharedGpuImageRequest([NtKeyedMutex], deviceLuid: luid);

        Assert.True(request.MatchesDevice(luid, [], out _));
        Assert.True(request.MatchesDevice([], [], out _));
        Assert.False(request.MatchesDevice([8, 7, 6, 5, 4, 3, 2, 1], [], out var reason));
        Assert.Contains("LUID", reason);
        Assert.True(new MediaSharedGpuImageRequest([NtKeyedMutex]).MatchesDevice(luid, luid, out _));
    }
}

public sealed class MediaSharedGpuImageSetTests
{
    [Fact]
    public void Handles_AreReleasedOnceAfterBothRetireAndCompleteImport()
    {
        List<bool> released = [];
        MediaSharedGpuImageSet set = Create(releaseHandles: released.Add);

        set.CompleteImport(handlesConsumed: true);
        Assert.Empty(released);
        set.Retire();
        set.Retire();
        set.CompleteImport(handlesConsumed: false);

        Assert.Equal([true], released);
        Assert.True(set.HandlesConsumed);
        Assert.True(set.IsRetired);
    }

    [Fact]
    public void ReleaseFrame_ReachesTheProducer()
    {
        List<(MediaSharedGpuImageFrame, bool)> releases = [];
        MediaSharedGpuImageSet set = Create(releaseFrame: (frame, consumed) => releases.Add((frame, consumed)));
        var frame = new MediaSharedGpuImageFrame(3, 1, 9, 1, 0);

        set.ReleaseFrame(frame, consumed: true);

        Assert.Equal([(frame, true)], releases);
    }

    [Fact]
    public void CompleteImport_PartialCount_TellsTheProducerWhichBuffersWereConsumed()
    {
        List<int> released = [];
        MediaSharedGpuImageSet set = CreateCounted(released.Add);

        set.CompleteImport(1);
        set.CompleteImport(2);
        set.Retire();

        Assert.Equal([1], released);
        Assert.Equal(1, set.ConsumedBufferCount);
        Assert.True(set.HandlesConsumed);
    }

    [Fact]
    public void CompleteImport_ClampsTheCountAndMapsTheBooleanForm()
    {
        List<int> released = [];
        MediaSharedGpuImageSet all = CreateCounted(released.Add);
        all.CompleteImport(handlesConsumed: true);
        all.Retire();
        MediaSharedGpuImageSet tooMany = CreateCounted(released.Add);
        tooMany.CompleteImport(7);
        tooMany.Retire();
        MediaSharedGpuImageSet none = CreateCounted(released.Add);
        none.CompleteImport(-1);
        none.Retire();

        Assert.Equal([2, 2, 0], released);
        Assert.False(none.HandlesConsumed);
    }

    [Fact]
    public void BooleanProducer_KeepsEveryHandleAfterAPartialImport()
    {
        List<bool> released = [];
        MediaSharedGpuImageSet set = Create(releaseHandles: released.Add);

        set.CompleteImport(1);
        set.Retire();

        // A producer that only knows "all or nothing" must not close a handle an importer took.
        Assert.Equal([true], released);
    }

    [Fact]
    public void ReportDeviceLost_ReachesTheProducerOnce_AndIsIgnoredWithoutACallback()
    {
        List<string> reports = [];
        MediaSharedGpuImageSet set = CreateCounted(static _ => { }, reports.Add);

        set.ReportDeviceLost("context lost");
        set.ReportDeviceLost("again");
        Create().ReportDeviceLost("nobody listens");

        Assert.Equal(["context lost"], reports);
    }

    [Fact]
    public void Constructor_RejectsAnEmptySet() =>
        Assert.Throws<ArgumentException>(() => new MediaSharedGpuImageSet(
            1, default, 4, 4, MediaSharedGpuImageFormat.Bgra8Unorm, [], static (_, _) => { }));

    private static MediaSharedGpuImageSet Create(
        Action<MediaSharedGpuImageFrame, bool>? releaseFrame = null,
        Action<bool>? releaseHandles = null) => new(
            3,
            new MediaSharedGpuImageSelection(MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, MediaSharedGpuImageSyncKinds.KeyedMutex, MediaSharedGpuSemaphoreHandleKind.None),
            64,
            32,
            MediaSharedGpuImageFormat.Bgra8Unorm,
            [new MediaSharedGpuImageBuffer(11), new MediaSharedGpuImageBuffer(12)],
            releaseFrame ?? (static (_, _) => { }),
            releaseHandles);

    private static MediaSharedGpuImageSet CreateCounted(Action<int> releaseConsumedHandles, Action<string>? deviceLost = null) => new(
        3,
        new MediaSharedGpuImageSelection(MediaSharedGpuImageHandleKind.VulkanOpaqueFd, MediaSharedGpuImageSyncKinds.HostSynchronized, MediaSharedGpuSemaphoreHandleKind.None),
        64,
        32,
        MediaSharedGpuImageFormat.Rgba8Unorm,
        [new MediaSharedGpuImageBuffer(11), new MediaSharedGpuImageBuffer(12)],
        static (_, _) => { },
        releaseConsumedHandles,
        deviceLost);
}

public sealed class MediaSharedGpuImageConsumerHandleTests
{
    [Fact]
    public void Descriptor_ResolvesToTheConsumerUntilDisposed()
    {
        var consumer = new NullConsumer();
        var handle = new MediaSharedGpuImageConsumerHandle(consumer);
        var surface = new MediaPresentationSurfaceDescriptor(
            MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
            MediaPresentationSurfaceRole.Primary,
            handle.CreateSurfaceDescriptor(),
            16,
            9);

        Assert.True(surface.IsValid);
        Assert.Equal(MediaNativeSurfaceTypeIds.SharedGpuImage, surface.NativeSurface.SurfaceTypeId);
        Assert.True(MediaSharedGpuImageConsumerHandle.TryResolve(surface, out IMediaSharedGpuImageConsumer? resolved));
        Assert.Same(consumer, resolved);

        handle.Dispose();
        Assert.False(handle.IsAllocated);
        Assert.False(MediaSharedGpuImageConsumerHandle.TryResolve(surface, out _));
        Assert.Throws<ObjectDisposedException>(handle.CreateSurfaceDescriptor);
    }

    [Fact]
    public void FrameSinkDescriptor_IsNotAConsumer()
    {
        using var sinkHandle = new MediaPresentationFrameSinkHandle(new NullSink());
        var surface = new MediaPresentationSurfaceDescriptor("primary", MediaPresentationSurfaceRole.Primary, sinkHandle.CreateSurfaceDescriptor(), 4, 4);

        Assert.False(MediaSharedGpuImageConsumerHandle.TryResolve(surface, out _));
    }

    private sealed class NullSink : IMediaPresentationFrameSink
    {
        public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride) { }
    }

    private sealed class NullConsumer : IMediaSharedGpuImageConsumer
    {
        public MediaSharedGpuImageRequest Request { get; } = new([]);
        public void OnImagesCreated(MediaSharedGpuImageSet images) { }
        public void OnFrameReady(MediaSharedGpuImageFrame frame) { }
        public void OnUnavailable(string reason) { }
    }
}
