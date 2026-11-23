using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Avalonia.Tests;

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
        (var session, var recorder) = RecordingSession.Create();
        var surface = new MediaSurface { Player = session, PreferGpuSharedSurface = true };
        var window = Show(surface);

        Assert.Equal(MediaNativeSurfaceTypeIds.ManagedBgraFrameSink, recorder.LastAttached!.NativeSurface.SurfaceTypeId);
        Assert.Equal(MediaSurfacePresentationPath.FrameSink, surface.PresentationPath);
        Assert.Contains("ICompositionGpuInterop", surface.PresentationPathReason);
        window.Close();
    }

    [AvaloniaFact]
    public void TheSharedGpuImagePath_IsTheDefault_AndTheEnvironmentVariableForcesTheFrameSink()
    {
        Assert.True(new MediaSurface().PreferGpuSharedSurface);
        Assert.True(new BackgroundSurface().Surface.PreferGpuSharedSurface);
        Assert.True(new KaraokeSurface().Surface.PreferGpuSharedSurface);

        // The diagnostic switch (the test player's --surface=framesink sets it) is read when a surface is created.
        Environment.SetEnvironmentVariable(MediaSurface.ForceFrameSinkVariable, "1");
        try
        {
            Assert.False(new MediaSurface().PreferGpuSharedSurface);
            Assert.False(new KaraokeSurface().Surface.PreferGpuSharedSurface);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MediaSurface.ForceFrameSinkVariable, null);
        }

        Assert.True(new MediaSurface().PreferGpuSharedSurface);
    }

    [AvaloniaFact]
    public void TheFrameSinkSwitch_UsesTheFrameSinkEvenWithInterop()
    {
        (var session, var recorder) = RecordingSession.Create();
        var surface = new MediaSurface
        {
            PreferGpuSharedSurface = false,
            GpuInteropProvider = FakeInterop.Provider(FakeInterop.Direct3D()),
            Player = session
        };
        var window = Show(surface);

        Assert.Equal(MediaNativeSurfaceTypeIds.ManagedBgraFrameSink, recorder.LastAttached!.NativeSurface.SurfaceTypeId);
        Assert.Equal("the frame sink is forced (FATZMEDIA_AVALONIA_FRAMESINK or a test switch)", surface.PresentationPathReason);
        window.Close();
    }

    [AvaloniaFact]
    public void SupportedInterop_AttachesASharedImageSurfaceWithTheCompositorRequest()
    {
        (var session, var recorder) = RecordingSession.Create();
        var surface = new MediaSurface
        {
            PreferGpuSharedSurface = true,
            GpuInteropProvider = FakeInterop.Provider(FakeInterop.Direct3D()),
            Player = session
        };
        var window = Show(surface);

        var attached = recorder.LastAttached!;
        Assert.Equal(MediaNativeSurfaceTypeIds.SharedGpuImage, attached.NativeSurface.SurfaceTypeId);
        Assert.Equal((320, 180), (attached.PixelWidth, attached.PixelHeight));
        Assert.Equal(MediaSurfacePresentationPath.SharedGpuImage, surface.PresentationPath);
        Assert.True(MediaSharedGpuImageConsumerHandle.TryResolve(attached, out var consumer));
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
        (var session, var recorder) = RecordingSession.Create();
        recorder.AttachResult = descriptor =>
        {
            if (descriptor.NativeSurface.SurfaceTypeId != MediaNativeSurfaceTypeIds.SharedGpuImage)
                return true;

            // What a renderer that cannot export does inside the attach.
            _ = MediaSharedGpuImageConsumerHandle.TryResolve(descriptor, out var consumer);
            consumer!.OnUnavailable("no common image handle kind");
            return false;
        };
        var surface = new MediaSurface
        {
            PreferGpuSharedSurface = true,
            GpuInteropProvider = FakeInterop.Provider(FakeInterop.Direct3D()),
            Player = session
        };
        var window = Show(surface);

        Assert.Equal(["attach 320x180", "attach 320x180"], recorder.Calls);
        Assert.Equal(MediaNativeSurfaceTypeIds.ManagedBgraFrameSink, recorder.LastAttached!.NativeSurface.SurfaceTypeId);
        Assert.Equal(MediaSurfacePresentationPath.FrameSink, surface.PresentationPath);
        Assert.Equal("no common image handle kind", surface.PresentationPathReason);
        window.Close();
    }

    [AvaloniaFact]
    public void LaterUnavailableReport_ReattachesWithTheFrameSinkAndStaysThereForTheSession()
    {
        (var session, var recorder) = RecordingSession.Create();
        var surface = new MediaSurface
        {
            PreferGpuSharedSurface = true,
            GpuInteropProvider = FakeInterop.Provider(FakeInterop.Direct3D()),
            Player = session
        };
        var window = Show(surface);
        _ = MediaSharedGpuImageConsumerHandle.TryResolve(recorder.LastAttached, out var consumer);

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
    public void ACompositorWithoutRenderThreadImport_UsesTheFrameSinkAndSaysWhy()
    {
        (var session, var recorder) = RecordingSession.Create();
        var surface = new MediaSurface
        {
            PreferGpuSharedSurface = true,
            GpuInteropProvider = FakeInterop.Provider(FakeInterop.Direct3D() with { Importer = null }),
            Player = session
        };
        var window = Show(surface);

        Assert.Equal(MediaSurfacePresentationPath.FrameSink, surface.PresentationPath);
        Assert.Contains("render-thread image import", surface.PresentationPathReason);
        Assert.Equal(MediaNativeSurfaceTypeIds.ManagedBgraFrameSink, recorder.LastAttached!.NativeSurface.SurfaceTypeId);
        window.Close();
    }

    [AvaloniaFact]
    public void ImagesTheCompositorCannotImport_FallBackAndHandTheHandlesBack()
    {
        (var session, var recorder) = RecordingSession.Create();
        var importer = new FakeImporter { ImportError = new InvalidOperationException("no such image") };
        var surface = new MediaSurface
        {
            PreferGpuSharedSurface = true,
            AutoHide = false,
            GpuInteropProvider = FakeInterop.Provider(FakeInterop.Direct3D(importer)),
            Player = session
        };
        var window = Show(surface);
        _ = MediaSharedGpuImageConsumerHandle.TryResolve(recorder.LastAttached, out var consumer);
        List<(MediaSharedGpuImageFrame Frame, bool Consumed)> released = [];
        int? consumedBuffers = null;
        var set = new MediaSharedGpuImageSet(
            1,
            new MediaSharedGpuImageSelection(MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, MediaSharedGpuImageSyncKinds.KeyedMutex, MediaSharedGpuSemaphoreHandleKind.None),
            320,
            180,
            MediaSharedGpuImageFormat.Bgra8Unorm,
            [new MediaSharedGpuImageBuffer(0x10), new MediaSharedGpuImageBuffer(0x20)],
            (frame, consumed) => { lock (released) released.Add((frame, consumed)); },
            consumed => consumedBuffers = consumed ? 2 : 0);

        consumer!.OnImagesCreated(set);
        consumer.OnFrameReady(new MediaSharedGpuImageFrame(1, 0, 1, MediaSharedGpuImageRing.ConsumerKey, MediaSharedGpuImageRing.ProducerKey));
        // The import runs on the render thread, on its next draw.
        RenderThread.Until(() => surface.PresentationPath == MediaSurfacePresentationPath.FrameSink);
        set.Retire();
        Dispatcher.UIThread.RunJobs();

        // Nothing could import the handles, so the producer keeps (and closes) them; the frame of
        // the unimported set is dropped (the renderer resets its ring when the surface detaches),
        // and the surface re-attaches with the frame sink.
        Assert.Equal(0, consumedBuffers);
        Assert.Empty(released);
        Assert.Contains("importing the shared images failed: no such image", surface.PresentationPathReason);
        Assert.Equal(MediaNativeSurfaceTypeIds.ManagedBgraFrameSink, recorder.LastAttached!.NativeSurface.SurfaceTypeId);
        window.Close();
    }

    [AvaloniaFact]
    public void TheRenderThread_ImportsTheSet_ThenSnapshotsEachFrameWithItsKeys_AndHandsItBack()
    {
        (var session, var recorder) = RecordingSession.Create();
        var importer = new FakeImporter();
        var surface = new MediaSurface
        {
            PreferGpuSharedSurface = true,
            AutoHide = false,
            GpuInteropProvider = FakeInterop.Provider(FakeInterop.Direct3D(importer)),
            Player = session
        };
        var window = Show(surface);
        _ = MediaSharedGpuImageConsumerHandle.TryResolve(recorder.LastAttached, out var consumer);
        List<(MediaSharedGpuImageFrame Frame, bool Consumed)> released = [];
        int? consumedBuffers = null;
        var set = new MediaSharedGpuImageSet(
            1,
            new MediaSharedGpuImageSelection(MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, MediaSharedGpuImageSyncKinds.KeyedMutex, MediaSharedGpuSemaphoreHandleKind.None),
            320,
            180,
            MediaSharedGpuImageFormat.Bgra8Unorm,
            [new MediaSharedGpuImageBuffer(0x10), new MediaSharedGpuImageBuffer(0x20)],
            (frame, consumed) => { lock (released) released.Add((frame, consumed)); },
            consumed => consumedBuffers = consumed ? 2 : 0);
        var first = new MediaSharedGpuImageFrame(1, 0, 1, MediaSharedGpuImageRing.ConsumerKey, MediaSharedGpuImageRing.ProducerKey);
        var second = new MediaSharedGpuImageFrame(1, 1, 2, MediaSharedGpuImageRing.ConsumerKey, MediaSharedGpuImageRing.ProducerKey);

        consumer!.OnImagesCreated(set);
        consumer.OnFrameReady(first);
        consumer.OnFrameReady(second);
        RenderThread.Until(() => importer.Snapshots.Count == 2);
        // The set reports its import once it is retired.
        set.Retire();

        // Imported on the render thread, both frames snapshotted in order and handed back as consumed,
        // and the surface stays on the shared images.
        Assert.Equal(2, consumedBuffers);
        Assert.Equal([first, second], importer.Snapshots);
        lock (released)
            Assert.Equal([(first, true), (second, true)], released);
        Assert.Equal(MediaSurfacePresentationPath.SharedGpuImage, surface.PresentationPath);
        window.Close();
    }

    [AvaloniaFact]
    public void WhileTheSurfaceIsHidden_FramesAreHandedBackUnconsumed_NotLeftInTheRing()
    {
        (var session, var recorder) = RecordingSession.Create();
        var importer = new FakeImporter();
        // Audio mode hides the surface, and Avalonia does not draw a hidden visual.
        var surface = new MediaSurface
        {
            PreferGpuSharedSurface = true,
            AutoHide = true,
            GpuInteropProvider = FakeInterop.Provider(FakeInterop.Direct3D(importer)),
            Player = session
        };
        var window = Show(surface);
        Task.Run(() => recorder.SwitchMode(MediaPlayerMode.Audio)).Wait();
        Dispatcher.UIThread.RunJobs();
        Assert.True(surface.IsPresentationSuspended);
        _ = MediaSharedGpuImageConsumerHandle.TryResolve(recorder.LastAttached, out var consumer);
        List<(MediaSharedGpuImageFrame Frame, bool Consumed)> released = [];
        var set = new MediaSharedGpuImageSet(
            1,
            new MediaSharedGpuImageSelection(MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, MediaSharedGpuImageSyncKinds.KeyedMutex, MediaSharedGpuSemaphoreHandleKind.None),
            320,
            180,
            MediaSharedGpuImageFormat.Bgra8Unorm,
            [new MediaSharedGpuImageBuffer(0x10), new MediaSharedGpuImageBuffer(0x20)],
            (frame, consumed) => { lock (released) released.Add((frame, consumed)); },
            _ => { });
        var frame = new MediaSharedGpuImageFrame(1, 0, 1, MediaSharedGpuImageRing.ConsumerKey, MediaSharedGpuImageRing.ProducerKey);

        consumer!.OnImagesCreated(set);
        consumer.OnFrameReady(frame);
        RenderThread.Until(() =>
        {
            lock (released)
                return released.Count == 1;
        });

        lock (released)
            Assert.Equal([(frame, false)], released);
        Assert.Empty(importer.Snapshots);
        Assert.Equal(MediaSurfacePresentationPath.SharedGpuImage, surface.PresentationPath);
        window.Close();
    }

    [AvaloniaFact]
    public void ASnapshotThatFails_ReleasesTheFrameUnconsumed_AndFallsBackToTheFrameSink()
    {
        (var session, var recorder) = RecordingSession.Create();
        var importer = new FakeImporter { SnapshotError = new InvalidOperationException("keyed mutex timed out") };
        var surface = new MediaSurface
        {
            PreferGpuSharedSurface = true,
            AutoHide = false,
            GpuInteropProvider = FakeInterop.Provider(FakeInterop.Direct3D(importer)),
            Player = session
        };
        var window = Show(surface);
        _ = MediaSharedGpuImageConsumerHandle.TryResolve(recorder.LastAttached, out var consumer);
        List<(MediaSharedGpuImageFrame Frame, bool Consumed)> released = [];
        var set = new MediaSharedGpuImageSet(
            1,
            new MediaSharedGpuImageSelection(MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, MediaSharedGpuImageSyncKinds.KeyedMutex, MediaSharedGpuSemaphoreHandleKind.None),
            320,
            180,
            MediaSharedGpuImageFormat.Bgra8Unorm,
            [new MediaSharedGpuImageBuffer(0x10), new MediaSharedGpuImageBuffer(0x20)],
            (frame, consumed) => { lock (released) released.Add((frame, consumed)); },
            _ => { });
        var frame = new MediaSharedGpuImageFrame(1, 0, 1, MediaSharedGpuImageRing.ConsumerKey, MediaSharedGpuImageRing.ProducerKey);

        consumer!.OnImagesCreated(set);
        consumer.OnFrameReady(frame);
        RenderThread.Until(() => surface.PresentationPath == MediaSurfacePresentationPath.FrameSink);

        lock (released)
            Assert.Equal([(frame, false)], released);
        Assert.Contains("showing a shared image failed: keyed mutex timed out", surface.PresentationPathReason);
        window.Close();
    }

    [AvaloniaFact]
    public void ContextLost_ReportsTheDeviceLoss_AndReturnsToSharedImagesOnceTheInteropWorks()
    {
        (var session, var recorder) = RecordingSession.Create();
        var queries = 0;
        var surface = new MediaSurface
        {
            PreferGpuSharedSurface = true,
            GpuInteropProvider = _ =>
                    {
                        queries++;
                        return new ValueTask<GpuInteropCapabilities?>(FakeInterop.Direct3D());
                    },
            Player = session
        };
        var window = Show(surface);
        _ = MediaSharedGpuImageConsumerHandle.TryResolve(recorder.LastAttached, out var consumer);
        List<string> deviceLossReports = [];
        var set = CountedSet(deviceLossReports.Add);
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
        (var session, var recorder) = RecordingSession.Create();
        var surface = new MediaSurface
        {
            PreferGpuSharedSurface = true,
            GpuInteropProvider = FakeInterop.Provider(FakeInterop.Direct3D()),
            Player = session
        };
        var window = Show(surface);
        _ = MediaSharedGpuImageConsumerHandle.TryResolve(recorder.LastAttached, out var consumer);
        ((AvaloniaSharedImageSurface)consumer!).ReportContextLost("context lost");
        Dispatcher.UIThread.RunJobs();

        // The renderer's device is still being re-created: it declines the shared images.
        var recovering = true;
        recorder.AttachResult = descriptor =>
        {
            if (!recovering || descriptor.NativeSurface.SurfaceTypeId != MediaNativeSurfaceTypeIds.SharedGpuImage)
                return true;

            _ = MediaSharedGpuImageConsumerHandle.TryResolve(descriptor, out var declined);
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
        (var session, var recorder) = RecordingSession.Create();
        var lost = false;
        var surface = new MediaSurface
        {
            PreferGpuSharedSurface = true,
            GpuInteropProvider = _ => new ValueTask<GpuInteropCapabilities?>(lost ? null : FakeInterop.Direct3D()),
            Player = session
        };
        var window = Show(surface);
        _ = MediaSharedGpuImageConsumerHandle.TryResolve(recorder.LastAttached, out var consumer);
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
        (var session, var recorder) = RecordingSession.Create();
        var answer = new TaskCompletionSource<GpuInteropCapabilities?>();
        var surface = new MediaSurface
        {
            PreferGpuSharedSurface = true,
            GpuInteropProvider = _ => new ValueTask<GpuInteropCapabilities?>(answer.Task),
            Player = session
        };
        var window = Show(surface);
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
        (var session, var recorder) = RecordingSession.Create();
        var interop = FakeInterop.Create([KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef], CompositionGpuImportedImageSynchronizationCapabilities.Automatic);
        var surface = new MediaSurface
        {
            PreferGpuSharedSurface = true,
            GpuInteropProvider = FakeInterop.Provider(interop),
            Player = session
        };
        var window = Show(surface);

        Assert.Equal(MediaNativeSurfaceTypeIds.ManagedBgraFrameSink, recorder.LastAttached!.NativeSurface.SurfaceTypeId);
        Assert.Contains("IOSurfaceRef", surface.PresentationPathReason);
        window.Close();
    }

    [Fact]
    public void CreateRequest_MapsHandleTypesAndSynchronizationInPreferenceOrder()
    {
        var request = AvaloniaGpuInterop.CreateRequest(
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

    static MediaSharedGpuImageSet CountedSet(Action<string> deviceLost) => new(
        1,
        new MediaSharedGpuImageSelection(MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, MediaSharedGpuImageSyncKinds.KeyedMutex, MediaSharedGpuSemaphoreHandleKind.None),
        320,
        180,
        MediaSharedGpuImageFormat.Bgra8Unorm,
        [new MediaSharedGpuImageBuffer(0x10), new MediaSharedGpuImageBuffer(0x20)],
        static (_, _) => { },
        static _ => { },
        deviceLost);

    static Window Show(MediaSurface surface)
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

        public static GpuInteropCapabilities Direct3D(FakeImporter? importer = null) => Create(
            [KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle, KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle],
            CompositionGpuImportedImageSynchronizationCapabilities.KeyedMutex,
            importer);

        public static GpuInteropCapabilities Create(
            IReadOnlyList<string> imageTypes,
            CompositionGpuImportedImageSynchronizationCapabilities synchronization,
            FakeImporter? importer = null) =>
            new(imageTypes, _ => synchronization, [], Luid, null, Interop: null, Importer: importer ?? new FakeImporter());

        public static Func<Compositor, ValueTask<GpuInteropCapabilities?>> Provider(GpuInteropCapabilities interop) =>
            _ => new ValueTask<GpuInteropCapabilities?>(interop);
    }

    /// <summary>
    /// The render-thread import without a GPU: records the snapshots it was asked for and hands back a small
    /// bitmap for each, or fails where a test asks it to.
    /// </summary>
    internal sealed class FakeImporter : IGpuImageImporter
    {
        readonly ConcurrentQueue<MediaSharedGpuImageFrame> _snapshots = new();

        public Exception? ImportError { get; init; }

        public Exception? SnapshotError { get; init; }

        public List<MediaSharedGpuImageFrame> Snapshots => [.. _snapshots];

        public IImportedGpuImages Import(MediaSharedGpuImageSet set) =>
            ImportError is { } error ? throw new GpuImportException(0, error) : new Images(this);

        sealed class Images(FakeImporter owner) : IImportedGpuImages
        {
            public Bitmap Snapshot(MediaSharedGpuImageFrame frame)
            {
                owner._snapshots.Enqueue(frame);

                return owner.SnapshotError is { } error
                    ? throw error
                    : new WriteableBitmap(new PixelSize(4, 4), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            }

            public void Dispose()
            {
            }
        }
    }
}
