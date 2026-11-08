using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace FatzBomb.MediaControl.Avalonia.Tests;

/// <summary>
/// Stage 027A slice 2: how <see cref="MediaSurface"/> chooses between the shared GPU image path and
/// the frame sink. The headless platform has no GPU interop, so the real compositor exercises the
/// fallback; a fake interop exercises the decision and the fallback after a failure.
/// </summary>
public sealed class SharedGpuImagePathTests
{
    [AvaloniaFact]
    public void HeadlessCompositor_FallsBackToTheFrameSinkAndSaysWhy()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        var surface = new MediaSurface { Player = session };
        Window window = Show(surface);

        Assert.Equal(MediaNativeSurfaceTypeIds.ManagedBgraFrameSink, recorder.LastAttached!.NativeSurface.SurfaceTypeId);
        Assert.Equal(MediaSurfacePresentationPath.FrameSink, surface.PresentationPath);
        Assert.Contains("ICompositionGpuInterop", surface.PresentationPathReason);
        window.Close();
    }

    [AvaloniaFact]
    public void PreferGpuSharedSurfaceOff_UsesTheFrameSinkEvenWithInterop()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        var surface = new MediaSurface { PreferGpuSharedSurface = false, GpuInteropProvider = FakeInterop.Provider(FakeInterop.Direct3D()) };
        surface.Player = session;
        Window window = Show(surface);

        Assert.Equal(MediaNativeSurfaceTypeIds.ManagedBgraFrameSink, recorder.LastAttached!.NativeSurface.SurfaceTypeId);
        Assert.Equal("PreferGpuSharedSurface is off", surface.PresentationPathReason);
        window.Close();
    }

    [AvaloniaFact]
    public void SupportedInterop_AttachesASharedImageSurfaceWithTheCompositorRequest()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        var surface = new MediaSurface { GpuInteropProvider = FakeInterop.Provider(FakeInterop.Direct3D()) };
        surface.Player = session;
        Window window = Show(surface);

        MediaPresentationSurfaceDescriptor attached = recorder.LastAttached!;
        Assert.Equal(MediaNativeSurfaceTypeIds.SharedGpuImage, attached.NativeSurface.SurfaceTypeId);
        Assert.Equal((320, 180), (attached.PixelWidth, attached.PixelHeight));
        Assert.Equal(MediaSurfacePresentationPath.SharedGpuImage, surface.PresentationPath);
        Assert.True(MediaSharedGpuImageConsumerHandle.TryResolve(attached, out IMediaSharedGpuImageConsumer? consumer));
        Assert.Equal(
            [
                new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, MediaSharedGpuImageSyncKinds.KeyedMutex),
                new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.D3D11TextureGlobalSharedHandle, MediaSharedGpuImageSyncKinds.KeyedMutex)
            ],
            consumer!.Request.ImageCapabilities);
        Assert.Equal(FakeInterop.Luid, consumer.Request.DeviceLuid.ToArray());
        window.Close();
    }

    [AvaloniaFact]
    public void RendererRejectingSharedImages_FallsBackToTheFrameSinkWithItsReason()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        recorder.AttachResult = descriptor =>
        {
            if (descriptor.NativeSurface.SurfaceTypeId != MediaNativeSurfaceTypeIds.SharedGpuImage)
                return true;

            // What a renderer that cannot export does inside the attach.
            _ = MediaSharedGpuImageConsumerHandle.TryResolve(descriptor, out IMediaSharedGpuImageConsumer? consumer);
            consumer!.OnUnavailable("no common image handle kind");
            return false;
        };
        var surface = new MediaSurface { GpuInteropProvider = FakeInterop.Provider(FakeInterop.Direct3D()) };
        surface.Player = session;
        Window window = Show(surface);

        Assert.Equal(["attach 320x180", "attach 320x180"], recorder.Calls);
        Assert.Equal(MediaNativeSurfaceTypeIds.ManagedBgraFrameSink, recorder.LastAttached!.NativeSurface.SurfaceTypeId);
        Assert.Equal(MediaSurfacePresentationPath.FrameSink, surface.PresentationPath);
        Assert.Equal("no common image handle kind", surface.PresentationPathReason);
        window.Close();
    }

    [AvaloniaFact]
    public void LaterUnavailableReport_ReattachesWithTheFrameSinkAndStaysThereForTheSession()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        var surface = new MediaSurface { GpuInteropProvider = FakeInterop.Provider(FakeInterop.Direct3D()) };
        surface.Player = session;
        Window window = Show(surface);
        _ = MediaSharedGpuImageConsumerHandle.TryResolve(recorder.LastAttached, out IMediaSharedGpuImageConsumer? consumer);

        // A device re-creation on another adapter, reported from the presentation thread.
        Task.Run(() => consumer!.OnUnavailable("the renderer GPU is not the compositor GPU")).Wait();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["attach 320x180", "detach", "attach 320x180"], recorder.Calls);
        Assert.Equal(MediaSurfacePresentationPath.FrameSink, surface.PresentationPath);
        Assert.Equal("the renderer GPU is not the compositor GPU", surface.PresentationPathReason);

        // Removing and re-adding the control keeps the frame sink for this session.
        window.Content = null;
        Dispatcher.UIThread.RunJobs();
        window.Content = surface;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(MediaNativeSurfaceTypeIds.ManagedBgraFrameSink, recorder.LastAttached!.NativeSurface.SurfaceTypeId);
        window.Close();
    }

    [AvaloniaFact]
    public void ImagesTheCompositorCannotImport_FallBackAndHandTheHandlesBack()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        var surface = new MediaSurface { GpuInteropProvider = FakeInterop.Provider(FakeInterop.Direct3D()) };
        surface.Player = session;
        Window window = Show(surface);
        _ = MediaSharedGpuImageConsumerHandle.TryResolve(recorder.LastAttached, out IMediaSharedGpuImageConsumer? consumer);
        List<(MediaSharedGpuImageFrame Frame, bool Consumed)> released = [];
        bool? handlesConsumed = null;
        var set = new MediaSharedGpuImageSet(
            1,
            new MediaSharedGpuImageSelection(MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, MediaSharedGpuImageSyncKinds.KeyedMutex, MediaSharedGpuSemaphoreHandleKind.None),
            320,
            180,
            MediaSharedGpuImageFormat.Bgra8Unorm,
            [new MediaSharedGpuImageBuffer(0x10), new MediaSharedGpuImageBuffer(0x20)],
            (frame, consumed) => { lock (released) released.Add((frame, consumed)); },
            consumed => handlesConsumed = consumed);

        consumer!.OnImagesCreated(set);
        var frame = new MediaSharedGpuImageFrame(1, 0, 1, MediaSharedGpuImageRing.ConsumerKey, MediaSharedGpuImageRing.ProducerKey);
        consumer.OnFrameReady(frame);
        Dispatcher.UIThread.RunJobs();
        set.Retire();
        Dispatcher.UIThread.RunJobs();

        // Nothing could import the handles, so the producer keeps (and closes) them; the frame of
        // the unimported set is dropped (the renderer resets its ring when the surface detaches),
        // and the surface re-attaches with the frame sink.
        Assert.False(handlesConsumed);
        Assert.Empty(released);
        Assert.Equal(MediaSurfacePresentationPath.FrameSink, surface.PresentationPath);
        Assert.Equal("the compositor has no GPU interop to import with", surface.PresentationPathReason);
        Assert.Equal(MediaNativeSurfaceTypeIds.ManagedBgraFrameSink, recorder.LastAttached!.NativeSurface.SurfaceTypeId);
        window.Close();
    }

    [AvaloniaFact]
    public void ContextLost_ReportsTheDeviceLoss_AndReturnsToSharedImagesOnceTheInteropWorks()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        var queries = 0;
        var surface = new MediaSurface
        {
            GpuInteropProvider = _ =>
            {
                queries++;
                return new ValueTask<GpuInteropCapabilities?>(FakeInterop.Direct3D());
            },
        };
        surface.Player = session;
        Window window = Show(surface);
        _ = MediaSharedGpuImageConsumerHandle.TryResolve(recorder.LastAttached, out IMediaSharedGpuImageConsumer? consumer);
        List<string> deviceLossReports = [];
        MediaSharedGpuImageSet set = CountedSet(deviceLossReports.Add);
        consumer!.OnImagesCreated(set);

        // What a PlatformGraphicsContextLostException from an update reports (ENG-039).
        ((AvaloniaSharedImageSurface)consumer).ReportContextLost("the compositor failed to show a shared image: context lost");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["the compositor failed to show a shared image: context lost"], deviceLossReports);
        Assert.Equal(MediaSurfacePresentationPath.FrameSink, surface.PresentationPath);
        Assert.Equal("the compositor failed to show a shared image: context lost", surface.PresentationPathReason);
        Assert.Equal(1, queries);

        // Not ruled out for the session: the next retry asks the compositor again and goes back.
        surface.RetryGpuAfterContextLoss();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, queries);
        Assert.Equal(MediaSurfacePresentationPath.SharedGpuImage, surface.PresentationPath);
        Assert.Equal(MediaNativeSurfaceTypeIds.SharedGpuImage, recorder.LastAttached!.NativeSurface.SurfaceTypeId);
        window.Close();
    }

    [AvaloniaFact]
    public void ContextLost_RendererStillRecovering_KeepsRetryingInsteadOfGivingUp()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        var surface = new MediaSurface { GpuInteropProvider = FakeInterop.Provider(FakeInterop.Direct3D()) };
        surface.Player = session;
        Window window = Show(surface);
        _ = MediaSharedGpuImageConsumerHandle.TryResolve(recorder.LastAttached, out IMediaSharedGpuImageConsumer? consumer);
        ((AvaloniaSharedImageSurface)consumer!).ReportContextLost("context lost");
        Dispatcher.UIThread.RunJobs();

        // The renderer's device is still being re-created: it declines the shared images.
        var recovering = true;
        recorder.AttachResult = descriptor =>
        {
            if (!recovering || descriptor.NativeSurface.SurfaceTypeId != MediaNativeSurfaceTypeIds.SharedGpuImage)
                return true;

            _ = MediaSharedGpuImageConsumerHandle.TryResolve(descriptor, out IMediaSharedGpuImageConsumer? declined);
            declined!.OnUnavailable("no usable Vulkan device");
            return false;
        };
        surface.RetryGpuAfterContextLoss();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(MediaSurfacePresentationPath.FrameSink, surface.PresentationPath);
        Assert.Equal("no usable Vulkan device", surface.PresentationPathReason);

        recovering = false;
        surface.RetryGpuAfterContextLoss();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(MediaSurfacePresentationPath.SharedGpuImage, surface.PresentationPath);
        window.Close();
    }

    [AvaloniaFact]
    public void ContextLost_InteropNeverReturns_KeepsTheFrameSinkAfterTheRetryLimit()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        var lost = false;
        var surface = new MediaSurface
        {
            GpuInteropProvider = _ => new ValueTask<GpuInteropCapabilities?>(lost ? null : FakeInterop.Direct3D()),
        };
        surface.Player = session;
        Window window = Show(surface);
        _ = MediaSharedGpuImageConsumerHandle.TryResolve(recorder.LastAttached, out IMediaSharedGpuImageConsumer? consumer);
        lost = true;
        ((AvaloniaSharedImageSurface)consumer!).ReportContextLost("context lost");
        Dispatcher.UIThread.RunJobs();

        for (var attempt = 0; attempt <= MediaSurface.GpuContextLossRetryAttempts; attempt++)
            surface.RetryGpuAfterContextLoss();
        lost = false;
        surface.RetryGpuAfterContextLoss();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(MediaSurfacePresentationPath.FrameSink, surface.PresentationPath);
        Assert.Equal(MediaNativeSurfaceTypeIds.ManagedBgraFrameSink, recorder.LastAttached!.NativeSurface.SurfaceTypeId);
        window.Close();
    }

    [AvaloniaFact]
    public void PendingInterop_AttachesOnlyOnceTheCompositorAnswers()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        var answer = new TaskCompletionSource<GpuInteropCapabilities?>();
        var surface = new MediaSurface { GpuInteropProvider = _ => new ValueTask<GpuInteropCapabilities?>(answer.Task) };
        surface.Player = session;
        Window window = Show(surface);
        Assert.Empty(recorder.Calls);

        answer.SetResult(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["attach 320x180"], recorder.Calls);
        Assert.Equal(MediaSurfacePresentationPath.FrameSink, surface.PresentationPath);
        window.Close();
    }

    [AvaloniaFact]
    public void InteropWithoutSupportedHandleKinds_FallsBack()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        GpuInteropCapabilities interop = FakeInterop.Create([KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef], CompositionGpuImportedImageSynchronizationCapabilities.Automatic);
        var surface = new MediaSurface { GpuInteropProvider = FakeInterop.Provider(interop) };
        surface.Player = session;
        Window window = Show(surface);

        Assert.Equal(MediaNativeSurfaceTypeIds.ManagedBgraFrameSink, recorder.LastAttached!.NativeSurface.SurfaceTypeId);
        Assert.Contains("IOSurfaceRef", surface.PresentationPathReason);
        window.Close();
    }

    [Fact]
    public void CreateRequest_MapsHandleTypesAndSynchronizationInPreferenceOrder()
    {
        MediaSharedGpuImageRequest request = AvaloniaGpuInterop.CreateRequest(
            [
                KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaquePosixFileDescriptor,
                KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef,
                KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle
            ],
            type => type == KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaquePosixFileDescriptor
                ? CompositionGpuImportedImageSynchronizationCapabilities.Semaphores | CompositionGpuImportedImageSynchronizationCapabilities.TimelineSemaphores
                : CompositionGpuImportedImageSynchronizationCapabilities.KeyedMutex | CompositionGpuImportedImageSynchronizationCapabilities.Automatic,
            [KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaquePosixFileDescriptor, KnownPlatformGraphicsExternalSemaphoreHandleTypes.MetalSharedEvent],
            null,
            [1, 2]);

        Assert.Equal(
            [
                new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.D3D11TextureGlobalSharedHandle, MediaSharedGpuImageSyncKinds.KeyedMutex | MediaSharedGpuImageSyncKinds.HostSynchronized),
                new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.VulkanOpaqueFd, MediaSharedGpuImageSyncKinds.Semaphores)
            ],
            request.ImageCapabilities);
        Assert.Equal([MediaSharedGpuSemaphoreHandleKind.VulkanOpaqueFd], request.SemaphoreHandleKinds);
        Assert.True(request.DeviceLuid.IsEmpty);
        Assert.Equal([1, 2], request.DeviceUuid.ToArray());
    }

    private static MediaSharedGpuImageSet CountedSet(Action<string> deviceLost) => new(
        1,
        new MediaSharedGpuImageSelection(MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, MediaSharedGpuImageSyncKinds.KeyedMutex, MediaSharedGpuSemaphoreHandleKind.None),
        320,
        180,
        MediaSharedGpuImageFormat.Bgra8Unorm,
        [new MediaSharedGpuImageBuffer(0x10), new MediaSharedGpuImageBuffer(0x20)],
        static (_, _) => { },
        static _ => { },
        deviceLost);

    private static Window Show(MediaSurface surface)
    {
        var window = new Window { Width = 320, Height = 180, Content = surface };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>
    /// A compositor whose interop offers handle kinds but cannot import (Avalonia's interop
    /// interfaces are not implementable outside Avalonia).
    /// </summary>
    internal static class FakeInterop
    {
        public static readonly byte[] Luid = [1, 0, 0, 0, 0, 0, 0, 0];

        public static GpuInteropCapabilities Direct3D() => Create(
            [KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle, KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle],
            CompositionGpuImportedImageSynchronizationCapabilities.KeyedMutex);

        public static GpuInteropCapabilities Create(IReadOnlyList<string> imageTypes, CompositionGpuImportedImageSynchronizationCapabilities synchronization) =>
            new(imageTypes, _ => synchronization, [], Luid, null, Interop: null);

        public static Func<Compositor, ValueTask<GpuInteropCapabilities?>> Provider(GpuInteropCapabilities interop) =>
            _ => new ValueTask<GpuInteropCapabilities?>(interop);
    }
}
