using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Graphics;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Core.Subtitle;
using FatzBomb.MediaControl.Core.Threading;
using FatzBomb.MediaControl.Pipeline;
using System.Collections.Concurrent;

namespace FatzBomb.MediaControl.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MediaPresentationCoordinatorCollection
{
    public const string Name = "Media presentation coordinator";
}

[Collection(MediaPresentationCoordinatorCollection.Name)]
public sealed class MediaPresentationCoordinatorTests
{
    [Fact]
    public void SurfaceLifecycle_UpdatesBackendAndRefreshesComposition()
    {
        var backend = new RecordingRendererBackend();
        using var coordinator = CreateCoordinator(backend, new TestMediaEngine());
        MediaPresentationSurfaceDescriptor surface = CreatePrimarySurface(1920, 1080);

        Assert.True(coordinator.AttachPresentationSurface(surface));
        Assert.Equal(1, backend.InitializeCount);
        Assert.Equal(surface, Assert.Single(backend.AttachedSurfaces));
        Assert.Equal("presentation surface attached", backend.PresentedFrames.Last().Reason);

        Assert.True(coordinator.ResizePresentationSurface(surface.Id, 640, 480));
        Assert.Equal((640, 480), (backend.ResizedSurfaces.Last().PixelWidth, backend.ResizedSurfaces.Last().PixelHeight));
        Assert.Equal("presentation surface resized", backend.PresentedFrames.Last().Reason);

        int presentationCount = backend.PresentedFrames.Count;
        Assert.True(coordinator.SetPresentationSurfaceLiveResize(surface.Id, true));
        Assert.Equal(presentationCount, backend.PresentedFrames.Count);
        Assert.True(coordinator.SetPresentationSurfaceLiveResize(surface.Id, false));
        Assert.Equal("presentation surface live resize ended", backend.PresentedFrames.Last().Reason);

        Assert.True(coordinator.DetachPresentationSurface(surface.Id));
        Assert.Equal(surface.Id, Assert.Single(backend.DetachedSurfaceIds));
        Assert.False(coordinator.ResizePresentationSurface(surface.Id, 800, 600));
    }

    [Fact]
    public void RejectedSharedGpuImageSurface_TellsTheConsumerWhy()
    {
        // Stage 027A slice 2: the adapter falls back to its frame sink when told.
        var backend = new RecordingRendererBackend { AcceptsSurfaces = false };
        using var coordinator = CreateCoordinator(backend, new TestMediaEngine());
        var consumer = new RecordingSharedImageConsumer();
        using var handle = new MediaSharedGpuImageConsumerHandle(consumer);
        var surface = new MediaPresentationSurfaceDescriptor(
            MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
            MediaPresentationSurfaceRole.Primary,
            handle.CreateSurfaceDescriptor(),
            320,
            180);

        Assert.False(coordinator.AttachPresentationSurface(surface));
        var reason = Assert.Single(consumer.UnavailableReasons);
        Assert.Contains("Test renderer", reason);
    }

    [Fact]
    public void RejectedWindowSurface_NotifiesNoConsumer()
    {
        var backend = new RecordingRendererBackend { AcceptsSurfaces = false };
        using var coordinator = CreateCoordinator(backend, new TestMediaEngine());

        Assert.False(coordinator.AttachPresentationSurface(CreatePrimarySurface(320, 180)));
    }

    [Fact]
    public void SubtitleRefresh_PreservesRetainedIntentAndForwardsFramePresented()
    {
        var backend = new RecordingRendererBackend();
        var engine = new TestMediaEngine { Content = MediaContent.Audio | MediaContent.Subtitle };
        using var coordinator = CreateCoordinator(backend, engine);
        var forwardedFrames = 0;
        coordinator.FramePresented += (_, _) => forwardedFrames++;

        Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1280, 720)));
        MediaCompositionFrame attached = backend.PresentedFrames.Last();
        Assert.True(attached.RetainedRefresh);
        Assert.Equal("presentation surface attached", attached.Reason);

        coordinator.RefreshCurrentPosition();
        MediaCompositionFrame refreshed = backend.PresentedFrames.Last();
        Assert.False(refreshed.RetainedRefresh);
        Assert.Equal("current position refreshed", refreshed.Reason);

        backend.RaiseFramePresented();
        Assert.Equal(1, forwardedFrames);
    }

    [Fact]
    public void TransparentBackground_ClearsWhereNothingIsDrawnToTransparent()
    {
        // Stage 043A: the default stays black; TransparentBackground switches the content clear.
        var backend = new RecordingRendererBackend();
        var engine = new TestMediaEngine { Content = MediaContent.Audio | MediaContent.Subtitle };
        using var coordinator = CreateCoordinator(backend, engine);
        Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1280, 720)));

        coordinator.RefreshCurrentPosition();
        MediaCompositionFrame black = backend.PresentedFrames.Last();
        Assert.Equal(MediaCompositionClearMode.Black, black.ClearMode);
        Assert.Equal(0xFF000000u, black.ClearArgb);

        coordinator.TransparentBackground = true;
        coordinator.RefreshCurrentPosition();
        MediaCompositionFrame transparent = backend.PresentedFrames.Last();
        Assert.Equal(MediaCompositionClearMode.Transparent, transparent.ClearMode);
        Assert.Equal(0u, transparent.ClearArgb);
        Assert.Equal((MediaCompositionClearMode.Black, 0xFF000000u), MediaPresentationCoordinator.ResolveContentClear(false));
    }

    [Fact]
    public void AudioOnlyRefresh_PresentsNothing()
    {
        // ENG-012: an audio file with nothing on screen re-presented an empty frame on every seek,
        // which Direct3D11 refused, logging "Composition present failed" each time.
        var backend = new RecordingRendererBackend();
        var engine = new TestMediaEngine { Content = MediaContent.Audio };
        using var coordinator = CreateCoordinator(backend, engine);
        Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1280, 720)));
        var presentationCount = backend.PresentedFrames.Count;

        coordinator.RefreshCurrentPosition();
        coordinator.RefreshCurrentPosition();

        Assert.Equal(presentationCount, backend.PresentedFrames.Count);
    }

    [Fact]
    public void FailedPresent_DiagnosticQuotesTheBackendReason()
    {
        var backend = new RecordingRendererBackend();
        var engine = new TestMediaEngine { Content = MediaContent.Audio | MediaContent.Subtitle };
        using var coordinator = CreateCoordinator(backend, engine);
        Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1280, 720)));
        var messages = new ConcurrentQueue<string>();
        void Record(MediaDiagnosticInfo info)
        {
            if (info.Category == "Composition")
                messages.Enqueue(info.Message);
        }

        backend.PresentFailureReason = "no active D3D11 presentation target";
        DiagnosticRouter.DiagnosticOccurred += Record;
        try
        {
            coordinator.RefreshCurrentPosition();
        }
        finally
        {
            DiagnosticRouter.DiagnosticOccurred -= Record;
        }

        Assert.Contains(
            "Composition present failed reason=current position refreshed (no active D3D11 presentation target)",
            messages);
    }

    [Fact]
    public void FailedPresents_AreReportedOncePerEpisode()
    {
        // A target going away refuses every present until one succeeds (seven lines at once in
        // the owner's 046A smoke when a smoke window closed): one warning per episode.
        var backend = new RecordingRendererBackend();
        var engine = new TestMediaEngine { Content = MediaContent.Audio | MediaContent.Subtitle };
        using var coordinator = CreateCoordinator(backend, engine);
        Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1280, 720)));
        var messages = new ConcurrentQueue<string>();
        var owner = new object();
        void Record(MediaDiagnosticInfo info)
        {
            // Only this test's diagnostics: other tests may raise Composition lines in parallel.
            if (info.Category == "Composition" && ReferenceEquals(info.Owner, owner))
                messages.Enqueue(info.Message);
        }

        DiagnosticRouter.DiagnosticOccurred += Record;
        try
        {
            using var scope = DiagnosticRouter.EnterOwner(owner);
            backend.PresentFailureReason = "no active D3D11 presentation target";
            coordinator.RefreshCurrentPosition();
            coordinator.RefreshCurrentPosition();
            coordinator.RefreshCurrentPosition();
            Assert.Single(messages);

            backend.PresentFailureReason = null;
            coordinator.RefreshCurrentPosition();
            backend.PresentFailureReason = "no active D3D11 presentation target";
            coordinator.RefreshCurrentPosition();
        }
        finally
        {
            DiagnosticRouter.DiagnosticOccurred -= Record;
        }

        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public void DeviceLostDiagnostic_ReinitializesSurfacesAndPresentsRecoveryFrame()
    {
        var backend = new RecordingRendererBackend();
        using var coordinator = CreateCoordinator(backend, new TestMediaEngine());
        Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1920, 1080)));

        DiagnosticRouter.Raise(
            MediaDiagnosticLevel.Error,
            backend.DeviceLostDiagnosticCategory,
            "simulated device loss");

        Assert.True(SpinWait.SpinUntil(
            () => backend.PresentedFrames.Any(static frame => frame.Reason == "device-lost recovery completed"),
            TimeSpan.FromSeconds(5)));
        Assert.True(backend.CleanupCount >= 1);
        Assert.True(backend.InitializeCount >= 2);
        Assert.True(backend.AttachedSurfaces.Count >= 2);
        Assert.True(backend.IsInitialized);
    }

    [Fact]
    public void DeviceLostDiagnostic_FromAnotherOwner_IsIgnored()
    {
        // ENG-025: another player's renderer has its own device; its loss is not this one's.
        var owner = new object();
        var backend = new RecordingRendererBackend();
        MediaPresentationCoordinator coordinator;
        using (DiagnosticRouter.EnterOwner(owner))
            coordinator = CreateCoordinator(backend, new TestMediaEngine());
        using (coordinator)
        {
            Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1920, 1080)));

            using (DiagnosticRouter.EnterOwner(new object()))
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, backend.DeviceLostDiagnosticCategory, "other player's device loss");

            Assert.False(SpinWait.SpinUntil(() => backend.CleanupCount > 0, TimeSpan.FromMilliseconds(250)));

            using (DiagnosticRouter.EnterOwner(owner))
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, backend.DeviceLostDiagnosticCategory, "own device loss");

            Assert.True(SpinWait.SpinUntil(
                () => backend.PresentedFrames.Any(static frame => frame.Reason == "device-lost recovery completed"),
                TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void PrimarySurfaceRefreshRate_ReachesTheVideoEngine()
    {
        // Stage 017A slice 3: video timing uses the rate of the monitor showing the primary surface.
        var backend = new RecordingRendererBackend { RefreshRateHz = 59.94 };
        var video = new TestVideoEngine();
        var coordinator = CreateCoordinator(backend, new TestMediaEngine { TestVideo = video });
        Func<double?> source = Assert.IsType<Func<double?>>(video.RendererRefreshRateSource);
        Assert.Null(source()); // no surface yet

        MediaPresentationSurfaceDescriptor surface = CreatePrimarySurface(1920, 1080);
        Assert.True(coordinator.AttachPresentationSurface(surface));
        Assert.Equal(59.94, source());
        Assert.Equal(surface, backend.LastRefreshRateSurface);

        Assert.True(coordinator.ResizePresentationSurface(surface.Id, 1280, 720));
        Assert.Equal(59.94, source());
        Assert.Equal(1280, backend.LastRefreshRateSurface!.PixelWidth);

        Assert.True(coordinator.DetachPresentationSurface(surface.Id));
        Assert.Null(source());

        coordinator.Dispose();
        Assert.Null(video.RendererRefreshRateSource);
    }

    [Fact]
    public void DetachingAnOldPrimary_KeepsTheNewPrimaryRefreshRate()
    {
        var backend = new RecordingRendererBackend { RefreshRateHz = 144 };
        var video = new TestVideoEngine();
        using var coordinator = CreateCoordinator(backend, new TestMediaEngine { TestVideo = video });
        MediaPresentationSurfaceDescriptor oldPrimary = CreatePrimarySurface(1920, 1080);
        var newPrimary = new MediaPresentationSurfaceDescriptor(
            "replacement-primary",
            MediaPresentationSurfaceRole.Primary,
            MediaNativeSurfaceDescriptor.Win32Hwnd((nint)0x5678),
            1920,
            1080);

        Assert.True(coordinator.AttachPresentationSurface(oldPrimary));
        Assert.True(coordinator.AttachPresentationSurface(newPrimary));
        Assert.True(coordinator.DetachPresentationSurface(oldPrimary.Id));

        Assert.Equal(144, video.RendererRefreshRateSource!());
        Assert.Equal(newPrimary.Id, backend.LastRefreshRateSurface!.Id);
    }

    [Fact]
    public void HardwareVideoFrame_IsComposedWhileASecondaryWindowIsAttached()
    {
        // The native-frame path presents to the primary only; a secondary window must get the
        // frame through composition, which fans out to every target.
        var backend = new RecordingRendererBackend { AcceptsNativeFrames = true };
        var video = new TestVideoEngine();
        using var coordinator = CreateCoordinator(backend, new TestMediaEngine { TestVideo = video, Content = MediaContent.Video });
        Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1920, 1080)));

        video.RaiseFrame(HardwareFrame());
        Assert.Equal(1, backend.NativeFramePresents);

        var secondary = new MediaPresentationSurfaceDescriptor(
            "projector",
            MediaPresentationSurfaceRole.Projector,
            MediaNativeSurfaceDescriptor.Win32Hwnd((nint)0x9abc),
            1280,
            720);
        Assert.True(coordinator.AttachPresentationSurface(secondary));
        var composedBefore = backend.PresentedFrames.Count;

        video.RaiseFrame(HardwareFrame());
        Assert.Equal(1, backend.NativeFramePresents);
        Assert.Equal("hardware video + secondary target", backend.PresentedFrames.Skip(composedBefore).Last().Reason);

        Assert.True(coordinator.DetachPresentationSurface(secondary.Id));
        video.RaiseFrame(HardwareFrame());
        Assert.Equal(2, backend.NativeFramePresents);
    }

    [Fact]
    public void PresentationTiming_GroupsSurfacesByDisplay()
    {
        // Stage 017A slice 5: surfaces on one monitor share a group; a surface without timing
        // follows the primary; with more than one display each group carries its target filter.
        var backend = new RecordingRendererBackend();
        var video = new TestVideoEngine();
        using var coordinator = CreateCoordinator(backend, new TestMediaEngine { TestVideo = video });
        MediaPresentationSurfaceDescriptor primary = CreatePrimarySurface(1920, 1080);
        MediaPresentationSurfaceDescriptor sameMonitor = Secondary("preview", 0x2001);
        MediaPresentationSurfaceDescriptor projector = Secondary("projector", 0x2002);
        MediaPresentationSurfaceDescriptor noTiming = Secondary("frame-sink", 0x2003);
        var display1 = new PresentationVsyncTiming(11, 1_000_000, 166_667);
        var display2 = new PresentationVsyncTiming(22, 1_000_500, 200_000);
        backend.VsyncTimings[primary.Id] = display1;
        backend.VsyncTimings[sameMonitor.Id] = display1;
        backend.VsyncTimings[projector.Id] = display2;
        foreach (MediaPresentationSurfaceDescriptor surface in new[] { primary, sameMonitor, projector, noTiming })
            Assert.True(coordinator.AttachPresentationSurface(surface));

        List<PresentationTimingGroup> groups = QueryGroups(video);

        Assert.Equal(2, groups.Count);
        PresentationTimingGroup first = groups[0];
        Assert.True(first.IncludesPrimary);
        Assert.Equal(display1, first.Timing);
        Assert.Equal(new[] { noTiming.Id, primary.Id, sameMonitor.Id }.Order(), first.SurfaceIds.Order());
        Assert.True(first.Targets!.IncludesPrimary);
        Assert.Equal(new[] { noTiming.Id, sameMonitor.Id }.Order(), first.Targets.SecondarySurfaceIds.Order());
        PresentationTimingGroup second = groups[1];
        Assert.False(second.IncludesPrimary);
        Assert.Equal(display2, second.Timing);
        Assert.Equal(projector.Id, Assert.Single(second.SurfaceIds));
        Assert.False(second.Targets!.IncludesPrimary);
        Assert.True(second.Targets.Includes(isPrimary: false, projector.Id));
        Assert.False(second.Targets.Includes(isPrimary: false, sameMonitor.Id));

        // The projector moves to the primary's monitor: one group, which needs no filter.
        backend.VsyncTimings[projector.Id] = display1;
        PresentationTimingGroup only = Assert.Single(QueryGroups(video));
        Assert.Null(only.Targets);
        Assert.Equal(4, only.SurfaceIds.Count);
    }

    [Fact]
    public void PresentationTiming_IsEmptyWithoutPrimaryTiming()
    {
        var backend = new RecordingRendererBackend();
        var video = new TestVideoEngine();
        using var coordinator = CreateCoordinator(backend, new TestMediaEngine { TestVideo = video });
        MediaPresentationSurfaceDescriptor projector = Secondary("projector", 0x2002);
        backend.VsyncTimings[projector.Id] = new PresentationVsyncTiming(22, 1_000_000, 200_000);
        Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1920, 1080)));
        Assert.True(coordinator.AttachPresentationSurface(projector));

        Assert.Empty(QueryGroups(video));
    }

    [Fact]
    public void AFrameForSomeDisplays_IsComposedForTheirTargetsOnly()
    {
        var backend = new RecordingRendererBackend { AcceptsNativeFrames = true };
        var video = new TestVideoEngine();
        using var coordinator = CreateCoordinator(backend, new TestMediaEngine { TestVideo = video, Content = MediaContent.Video });
        Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1920, 1080)));
        Assert.True(coordinator.AttachPresentationSurface(Secondary("projector", 0x2002)));
        var targets = new MediaCompositionTargetFilter(includesPrimary: false, ["projector"]);

        video.RaiseFrame(HardwareFrame(targets));
        Assert.Same(targets, backend.PresentedFrames.Last().Targets);

        // The filter belongs to that frame only.
        video.RaiseFrame(HardwareFrame());
        Assert.Null(backend.PresentedFrames.Last().Targets);
    }

    [Fact]
    public void SelectingASubtitle_WhilePlayingStartsAndStopsTheRenderLoop()
    {
        // ENG-022: the subtitle flag follows the selected track, so the loop follows the flag.
        var backend = new RecordingRendererBackend();
        var engine = new TestMediaEngine { Content = MediaContent.Audio };
        using var coordinator = CreateCoordinator(backend, engine);
        Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1280, 720)));
        coordinator.StartRendering();
        Assert.False(coordinator.IsRenderLoopRunning);

        engine.Content = MediaContent.Audio | MediaContent.Subtitle;
        coordinator.RefreshCurrentPosition();
        Assert.True(coordinator.IsRenderLoopRunning);

        engine.Content = MediaContent.Audio;
        coordinator.RefreshCurrentPosition();
        Assert.False(coordinator.IsRenderLoopRunning);
        coordinator.StopRendering();
    }

    [Fact]
    public void SoftwareCompositions_ArePresentedInTheOrderTheyWereBuilt()
    {
        // ENG-023: a composition built first but slow to present could land after a newer one.
        var backend = new RecordingRendererBackend();
        using var coordinator = CreateCoordinator(backend, new TestMediaEngine { Content = MediaContent.Audio | MediaContent.Subtitle });
        Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1280, 720)));
        var baseline = backend.PresentedFrames.Count;

        using var firstEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        backend.BeforePresent = frame =>
        {
            if (frame.Reason != "current position refreshed")
                return;

            firstEntered.Set();
            _ = release.Wait(TimeSpan.FromSeconds(10));
        };

        var first = new Thread(coordinator.RefreshCurrentPosition);
        first.Start();
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));
        var second = new Thread(coordinator.StartRendering);
        second.Start();

        // Without ordering the second composition is built and presented while the first waits.
        _ = second.Join(TimeSpan.FromMilliseconds(300));
        release.Set();
        first.Join();
        second.Join();
        coordinator.StopRendering();

        Assert.Equal(
            ["current position refreshed", "presentation started"],
            backend.PresentedFrames.Skip(baseline).Take(2).Select(static frame => frame.Reason).ToArray());
    }

    [Fact]
    public void Presents_StayInBuildOrder_AcrossPaths()
    {
        // ENG-039: every present path takes the present gate, so a clear cannot overtake a
        // composition still presenting (it took only _lock before, which a software present no
        // longer held while it presented).
        var backend = new RecordingRendererBackend();
        using var coordinator = CreateCoordinator(backend, new TestMediaEngine { Content = MediaContent.Audio | MediaContent.Subtitle });
        Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1280, 720)));
        var baseline = backend.PresentedFrames.Count;

        using var firstEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        backend.BeforePresent = frame =>
        {
            if (frame.Reason != "current position refreshed")
                return;

            firstEntered.Set();
            _ = release.Wait(TimeSpan.FromSeconds(10));
        };

        var first = new Thread(coordinator.RefreshCurrentPosition);
        first.Start();
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));
        // Stopped media: StopRendering clears the composition (a present of its own).
        var second = new Thread(coordinator.StopRendering);
        second.Start();

        _ = second.Join(TimeSpan.FromMilliseconds(300));
        release.Set();
        first.Join();
        second.Join();

        Assert.Equal(
            ["current position refreshed", "presentation stopped"],
            backend.PresentedFrames.Skip(baseline).Select(static frame => frame.Reason).ToArray());
    }

    [Fact]
    public void SurfaceCalls_DoNotWaitForABlockedPresent()
    {
        // ENG-039 (GPU-reset smoke 3c): a present stuck in the renderer held the coordinator lock,
        // so the UI thread's next resize waited for it and the player froze.
        var time = new ManualTimeProvider();
        var backend = new RecordingRendererBackend();
        using var coordinator = CreateCoordinator(backend, new TestMediaEngine { Content = MediaContent.Audio | MediaContent.Subtitle }, time);
        MediaPresentationSurfaceDescriptor surface = CreatePrimarySurface(1280, 720);
        Assert.True(coordinator.AttachPresentationSurface(surface));

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        backend.BeforePresent = frame =>
        {
            if (frame.Reason != "current position refreshed")
                return;

            entered.Set();
            _ = release.Wait(TimeSpan.FromSeconds(10));
        };

        var stuck = new Thread(coordinator.RefreshCurrentPosition);
        stuck.Start();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            time.Advance(TimeSpan.FromMilliseconds(300));

            var results = new ConcurrentQueue<bool>();
            var surfaceCalls = new Thread(() =>
            {
                results.Enqueue(coordinator.ResizePresentationSurface(surface.Id, 1920, 1080));
                results.Enqueue(coordinator.SetPresentationSurfaceLiveResize(surface.Id, true));
                results.Enqueue(coordinator.SetPresentationSurfaceLiveResize(surface.Id, false));
                coordinator.BaseVisualTransform = new BaseVisualTransform { Brightness = 0.5f };
                results.Enqueue(coordinator.BaseVisualTransform.Brightness == 0.5f);
            });
            surfaceCalls.Start();
            var returned = surfaceCalls.Join(TimeSpan.FromSeconds(2));
            release.Set();
            stuck.Join();
            surfaceCalls.Join();

            Assert.True(returned, "a surface call waited for the blocked present");
            Assert.Equal([true, true, true, true], results);
        }
        finally
        {
            release.Set();
        }

        // The resize's present was left to the thread pool and runs once the gate is free.
        Assert.True(SpinWait.SpinUntil(
            () => backend.PresentedFrames.Any(static frame => frame.Reason is "presentation surface resized" or "presentation surface live resize ended"),
            TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void HealthProbe_LostWhileIdle_RecoversWithoutAPresent()
    {
        // ENG-039: nothing presented after the GPU reset, so no present ever reported the loss.
        var owner = new object();
        var time = new ManualTimeProvider();
        var backend = new RecordingRendererBackend();
        var diagnostics = new ConcurrentQueue<MediaDiagnosticInfo>();
        void Record(MediaDiagnosticInfo info)
        {
            if (ReferenceEquals(info.Owner, owner))
                diagnostics.Enqueue(info);
        }

        MediaPresentationCoordinator coordinator;
        using (DiagnosticRouter.EnterOwner(owner))
            coordinator = CreateCoordinator(backend, new TestMediaEngine(), time);
        DiagnosticRouter.DiagnosticOccurred += Record;
        try
        {
            using (coordinator)
            {
                var recovered = 0;
                coordinator.DeviceRecovered += (_, _) => Interlocked.Increment(ref recovered);
                Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1280, 720)));
                var presents = backend.PresentedFrames.Count;
                backend.DeviceLost = true;

                time.Advance(TimeSpan.FromSeconds(1));

                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref recovered) == 1, TimeSpan.FromSeconds(5)));
                Assert.Equal(presents + 2, backend.PresentedFrames.Count); // the stop clear and the recovery frame
                Assert.Equal("device-lost recovery completed", backend.PresentedFrames.Last().Reason);
                Assert.Equal(1, backend.CleanupCount);
                Assert.Equal(2, backend.InitializeCount);
                Assert.Equal(2, backend.AttachedSurfaces.Count);
                Assert.Contains(diagnostics, static info =>
                    info.Level == MediaDiagnosticLevel.Error && info.Message == "GPU device lost (test loss); recovering.");
                Assert.Contains(diagnostics, static info => info.Message == "GPU device recovered after 1 attempt(s).");
            }
        }
        finally
        {
            DiagnosticRouter.DiagnosticOccurred -= Record;
        }
    }

    [Fact]
    public void FailedPresent_ProbesImmediately()
    {
        var time = new ManualTimeProvider();
        var backend = new RecordingRendererBackend();
        using var coordinator = CreateCoordinator(backend, new TestMediaEngine { Content = MediaContent.Audio | MediaContent.Subtitle }, time);
        Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1280, 720)));
        backend.DeviceLost = true;
        backend.PresentFailureReason = "DXGI_ERROR_DEVICE_REMOVED";

        coordinator.RefreshCurrentPosition();
        Assert.Equal(0, Volatile.Read(ref backend.ProbeCount));

        // No full health interval passes: the failed present brought the probe forward.
        time.Advance(TimeSpan.Zero);
        backend.PresentFailureReason = null;

        Assert.True(Volatile.Read(ref backend.ProbeCount) >= 1);
        Assert.True(SpinWait.SpinUntil(
            () => backend.PresentedFrames.Any(static frame => frame.Reason == "device-lost recovery completed"),
            TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void HealthProbe_NoSurfaces_DoesNotEscalate()
    {
        var time = new ManualTimeProvider();
        var backend = new RecordingRendererBackend();
        using var coordinator = CreateCoordinator(backend, new TestMediaEngine(), time);
        MediaPresentationSurfaceDescriptor surface = CreatePrimarySurface(1280, 720);
        Assert.True(coordinator.AttachPresentationSurface(surface));
        Assert.True(coordinator.DetachPresentationSurface(surface.Id));
        backend.DeviceLost = true;

        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(0, Volatile.Read(ref backend.ProbeCount));
        Assert.Equal(0, backend.CleanupCount);
    }

    [Fact]
    public void HealthProbe_Healthy_NeverCleansUp()
    {
        var time = new ManualTimeProvider();
        var backend = new RecordingRendererBackend();
        using var coordinator = CreateCoordinator(backend, new TestMediaEngine(), time);
        Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1280, 720)));

        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(5, Volatile.Read(ref backend.ProbeCount));
        Assert.Equal(0, backend.CleanupCount);
    }

    [Fact]
    public void FailedRecovery_IsRetriedWhileASurfaceIsAttached()
    {
        var time = new ManualTimeProvider();
        var backend = new RecordingRendererBackend { InitializeSucceeds = true };
        using var coordinator = CreateCoordinator(backend, new TestMediaEngine(), time);
        coordinator.RecoveryBackoff = [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)];
        Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1280, 720)));
        backend.InitializeSucceeds = false;
        backend.DeviceLost = true;

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(SpinWait.SpinUntil(() => backend.CleanupCount == 3 && !coordinator.IsRecoveryRunning, TimeSpan.FromSeconds(5)));

        // Given up: retried after 10 s, not at every tick.
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(3, backend.CleanupCount);
        backend.InitializeSucceeds = true;
        time.Advance(TimeSpan.FromSeconds(5));

        Assert.True(SpinWait.SpinUntil(
            () => backend.PresentedFrames.Any(static frame => frame.Reason == "device-lost recovery completed"),
            TimeSpan.FromSeconds(5)));
        Assert.Equal(4, backend.CleanupCount);
    }

    [Fact]
    public void HardwareFrames_AreDroppedWhileRecovering()
    {
        var time = new ManualTimeProvider();
        var backend = new RecordingRendererBackend { AcceptsNativeFrames = true, InitializeSucceeds = true };
        var video = new TestVideoEngine();
        using var coordinator = CreateCoordinator(backend, new TestMediaEngine { TestVideo = video, Content = MediaContent.Video }, time);
        coordinator.RecoveryBackoff = [];
        Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1920, 1080)));
        backend.InitializeSucceeds = false;
        backend.DeviceLost = true;
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(SpinWait.SpinUntil(() => backend.CleanupCount == 1 && !coordinator.IsRecoveryRunning, TimeSpan.FromSeconds(5)));
        var presents = backend.NativeFramePresents;

        video.RaiseFrame(HardwareFrame());

        Assert.Equal(presents, backend.NativeFramePresents);
    }

    [Fact]
    public void DeviceLost_DuringDispose_DoesNotThrow()
    {
        var time = new ManualTimeProvider();
        var backend = new RecordingRendererBackend();
        var coordinator = CreateCoordinator(backend, new TestMediaEngine(), time);
        Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1280, 720)));
        coordinator.Dispose();
        backend.DeviceLost = true;

        coordinator.RequestDeviceLostRecovery("after dispose");
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(1, backend.CleanupCount); // Dispose's own cleanup only
        Assert.False(coordinator.IsRecoveryRunning);
    }

    [Fact]
    public void BlockedRendererCall_RaisesOneStallDiagnostic()
    {
        var owner = new object();
        var time = new ManualTimeProvider();
        var backend = new RecordingRendererBackend();
        var messages = new ConcurrentQueue<string>();
        void Record(MediaDiagnosticInfo info)
        {
            if (ReferenceEquals(info.Owner, owner) && info.Category == "Presentation")
                messages.Enqueue(info.Message);
        }

        MediaPresentationCoordinator coordinator;
        using (DiagnosticRouter.EnterOwner(owner))
            coordinator = CreateCoordinator(backend, new TestMediaEngine { Content = MediaContent.Audio | MediaContent.Subtitle }, time);
        DiagnosticRouter.DiagnosticOccurred += Record;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            using (coordinator)
            {
                Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1280, 720)));
                backend.BeforePresent = frame =>
                {
                    if (frame.Reason != "current position refreshed")
                        return;

                    entered.Set();
                    _ = release.Wait(TimeSpan.FromSeconds(10));
                };

                var stuck = new Thread(coordinator.RefreshCurrentPosition);
                stuck.Start();
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

                time.Advance(TimeSpan.FromSeconds(2));
                Assert.Empty(messages);
                time.Advance(TimeSpan.FromSeconds(4));
                release.Set();
                stuck.Join();

                Assert.Equal(2, messages.Count);
                Assert.Equal("Renderer call has not returned for 3 s (PresentComposition); presentation is blocked.", messages.First());
                Assert.StartsWith("Renderer call returned after 6", messages.Last());
            }
        }
        finally
        {
            release.Set();
            DiagnosticRouter.DiagnosticOccurred -= Record;
        }
    }

    [Fact]
    public void HardwareBackground_FailedPresent_IsNotRetained()
    {
        // ENG-039 (smoke test 6): the background was marked retained before its present, which
        // failed with no target yet, so the next refresh asked for a background the renderer
        // never received ("background layer could not be prepared").
        var owner = new object();
        var backend = new RecordingRendererBackend { AcceptsNativeFrames = true, PresentFailureReason = "no active D3D11 presentation target" };
        var engine = new TestMediaEngine { Content = MediaContent.Audio };
        var warnings = new ConcurrentQueue<string>();
        void Record(MediaDiagnosticInfo info)
        {
            if (ReferenceEquals(info.Owner, owner) && info.Category == "Composition")
                warnings.Enqueue(info.Message);
        }

        MediaPresentationCoordinator coordinator;
        using (DiagnosticRouter.EnterOwner(owner))
            coordinator = CreateCoordinator(backend, engine);
        DiagnosticRouter.DiagnosticOccurred += Record;
        try
        {
            using (coordinator)
            using (DiagnosticRouter.EnterOwner(owner))
            {
                engine.RaiseBackgroundFrame(HardwareFrame());
                backend.PresentFailureReason = null;

                Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1280, 720)));

                MediaCompositionFrame attached = backend.PresentedFrames.Last();
                Assert.Equal("presentation surface attached", attached.Reason);
                Assert.False(attached.UseRetainedBackground);
                Assert.Empty(warnings);
            }
        }
        finally
        {
            DiagnosticRouter.DiagnosticOccurred -= Record;
        }
    }

    [Fact]
    public void HardwareBackground_PresentedFrame_IsRetainedForRefresh()
    {
        var backend = new RecordingRendererBackend { AcceptsNativeFrames = true };
        var engine = new TestMediaEngine { Content = MediaContent.Audio };
        using var coordinator = CreateCoordinator(backend, engine);
        Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1280, 720)));

        engine.RaiseBackgroundFrame(HardwareFrame());
        MediaCompositionFrame hardware = backend.PresentedFrames.Last();
        Assert.Equal("core hardware background frame", hardware.Reason);

        coordinator.RefreshCurrentPosition();

        MediaCompositionFrame refreshed = backend.PresentedFrames.Last();
        Assert.Equal("current position refreshed", refreshed.Reason);
        Assert.True(refreshed.UseRetainedBackground);
        Assert.Equal(hardware.BackgroundRevision, refreshed.BackgroundRevision);
        Assert.Equal((1920, 1080), (refreshed.BackgroundWidth, refreshed.BackgroundHeight));
    }

    [Fact]
    public void StoredHardwareBackground_WithoutPresent_IsNotRetained()
    {
        // CD+G waiting to be shown keeps the background frame without presenting it.
        var backend = new RecordingRendererBackend { AcceptsNativeFrames = true };
        var engine = new TestMediaEngine { Content = MediaContent.Audio | MediaContent.Cdg };
        using var coordinator = CreateCoordinator(backend, engine);
        Assert.True(coordinator.AttachPresentationSurface(CreatePrimarySurface(1280, 720)));
        var presents = backend.PresentedFrames.Count;

        engine.RaiseBackgroundFrame(HardwareFrame());
        Assert.Equal(presents, backend.PresentedFrames.Count);

        engine.Content = MediaContent.Audio;
        coordinator.RefreshCurrentPosition();

        MediaCompositionFrame refreshed = backend.PresentedFrames.Last();
        Assert.Equal("current position refreshed", refreshed.Reason);
        Assert.False(refreshed.UseRetainedBackground);
    }

    private static List<PresentationTimingGroup> QueryGroups(TestVideoEngine video)
    {
        var groups = new List<PresentationTimingGroup>();
        video.PresentationTimingSource!(groups);
        return groups;
    }

    private static MediaPresentationSurfaceDescriptor Secondary(string id, nint hwnd) => new(
        id,
        MediaPresentationSurfaceRole.Projector,
        MediaNativeSurfaceDescriptor.Win32Hwnd(hwnd),
        1280,
        720);

    private static VideoFrameEventArgs HardwareFrame(MediaCompositionTargetFilter? targets) => new()
    {
        Width = 1920,
        Height = 1080,
        Stride = 0,
        PtsSeconds = 0,
        NativeResource = new NativeVideoFrameResource(
            new NativeVideoDeviceResource(MediaGraphicsBackendId.Direct3D11, DeviceHandle: 101),
            ResourceHandle: 303,
            SubresourceIndex: 0),
        Targets = targets,
    };

    private static VideoFrameEventArgs HardwareFrame() => new()
    {
        Width = 1920,
        Height = 1080,
        Stride = 0,
        PtsSeconds = 0,
        NativeResource = new NativeVideoFrameResource(
            new NativeVideoDeviceResource(MediaGraphicsBackendId.Direct3D11, DeviceHandle: 101),
            ResourceHandle: 303,
            SubresourceIndex: 0),
    };

    private static MediaPresentationCoordinator CreateCoordinator(
        RecordingRendererBackend backend,
        TestMediaEngine engine,
        TimeProvider? timeProvider = null) => new(
            backend,
            new StubSubtitleRasterizer(),
            engine,
            MediaPlayerConfiguration.Auto,
            new SystemPrecisionWaiterFactory(),
            // Without a test clock the health timer never fires during a test.
            timeProvider ?? new ManualTimeProvider());

    /// <summary>A clock that moves only when the test advances it; timers fire on the test thread.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _now;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => Volatile.Read(ref _now);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            _ = timer.Change(dueTime, period);
            lock (_gate)
                _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            var target = Volatile.Read(ref _now) + (long)duration.TotalMilliseconds;
            while (true)
            {
                ManualTimer? due;
                lock (_gate)
                    due = _timers.Where(timer => timer.DueAt is { } at && at <= target).MinBy(timer => timer.DueAt);
                if (due is null)
                    break;

                Volatile.Write(ref _now, Math.Max(Volatile.Read(ref _now), due.DueAt!.Value));
                due.Fire();
            }

            Volatile.Write(ref _now, target);
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private long _period;

            public long? DueAt { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _period = period == Timeout.InfiniteTimeSpan ? 0 : (long)period.TotalMilliseconds;
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetTimestamp() + (long)dueTime.TotalMilliseconds;
                return true;
            }

            public void Fire()
            {
                DueAt = _period > 0 ? DueAt + _period : null;
                callback(state);
            }

            public void Dispose() => DueAt = null;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static MediaPresentationSurfaceDescriptor CreatePrimarySurface(int width, int height) => new(
        MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
        MediaPresentationSurfaceRole.Primary,
        MediaNativeSurfaceDescriptor.Win32Hwnd((nint)0x1234),
        width,
        height);

    private sealed class RecordingSharedImageConsumer : IMediaSharedGpuImageConsumer
    {
        public ConcurrentQueue<string> UnavailableReasons { get; } = [];

        public MediaSharedGpuImageRequest Request { get; } = new(
            [new MediaSharedGpuImageCapability(MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, MediaSharedGpuImageSyncKinds.KeyedMutex)]);

        public void OnImagesCreated(MediaSharedGpuImageSet images) => images.CompleteImport(handlesConsumed: false);

        public void OnFrameReady(MediaSharedGpuImageFrame frame) { }

        public void OnUnavailable(string reason) => UnavailableReasons.Enqueue(reason);
    }

    private sealed class StubSubtitleRasterizer : ISubtitleRasterizer
    {
        public SubtitleRasterResult Render(string text, int width, int height) => default;
        public SubtitleRasterResult RenderKaraoke(KaraokeCueTiming timing, TimeSpan position, int width, int height) => default;
        public void Dispose() { }
    }

    private sealed class RecordingRendererBackend : IMediaRendererBackend
    {
        public ConcurrentQueue<MediaPresentationSurfaceDescriptor> AttachedSurfaces { get; } = [];
        public ConcurrentQueue<MediaPresentationSurfaceDescriptor> ResizedSurfaces { get; } = [];
        public ConcurrentQueue<string> DetachedSurfaceIds { get; } = [];
        public ConcurrentQueue<MediaCompositionFrame> PresentedFrames { get; } = [];
        public int InitializeCount { get; private set; }
        public int CleanupCount { get; private set; }

        public event EventHandler? FramePresented;

        public string BackendName => "Test";
        public string DeviceLostDiagnosticCategory => "Test.DeviceLost";
        public string DeviceLostRecoveryFailedDiagnosticCategory => "Test.DeviceLost.RecoveryFailed";
        public bool IsInitialized { get; private set; }
        public nint NativeDeviceHandle => 0;

        public bool InitializeSucceeds { get; set; } = true;

        public bool Initialize()
        {
            InitializeCount++;
            IsInitialized = InitializeSucceeds;
            return InitializeSucceeds;
        }

        public volatile bool DeviceLost;
        public int ProbeCount;

        public bool ProbeDeviceLost(out string? reason)
        {
            _ = Interlocked.Increment(ref ProbeCount);
            reason = DeviceLost ? "test loss" : null;
            return DeviceLost;
        }

        public bool AcceptsSurfaces { get; set; } = true;

        public bool AttachSurface(MediaPresentationSurfaceDescriptor surface)
        {
            AttachedSurfaces.Enqueue(surface);
            return AcceptsSurfaces;
        }

        public bool ResizeSurface(MediaPresentationSurfaceDescriptor surface)
        {
            ResizedSurfaces.Enqueue(surface);
            return true;
        }

        public bool SetSurfaceLiveResize(MediaPresentationSurfaceDescriptor surface, bool isResizing) => true;

        public bool DetachSurface(MediaPresentationSurfaceDescriptor surface)
        {
            DetachedSurfaceIds.Enqueue(surface.Id);
            return true;
        }

        public void SetPresentationStretch(MediaCompositionStretchMode stretchMode) { }
        public bool AcceptsNativeFrames { get; set; }
        public int NativeFramePresents { get; private set; }

        public bool PresentNativeFrame(VideoFrameEventArgs frame)
        {
            if (AcceptsNativeFrames)
                NativeFramePresents++;
            return AcceptsNativeFrames;
        }
        public void ClearCompositionBackgroundFrame() { }

        public string? PresentFailureReason { get; set; }

        public string? LastPresentFailureReason => PresentFailureReason;

        public double? RefreshRateHz { get; set; }
        public MediaPresentationSurfaceDescriptor? LastRefreshRateSurface { get; private set; }
        public Dictionary<string, PresentationVsyncTiming> VsyncTimings { get; } = [];

        public bool TryGetSurfaceVsyncTiming(MediaPresentationSurfaceDescriptor surface, out PresentationVsyncTiming timing) =>
            VsyncTimings.TryGetValue(surface.Id, out timing);

        public double? GetSurfaceRefreshRateHz(MediaPresentationSurfaceDescriptor surface)
        {
            LastRefreshRateSurface = surface;
            return RefreshRateHz;
        }

        public Action<MediaCompositionFrame>? BeforePresent { get; set; }

        public bool PresentComposition(MediaCompositionFrame frame)
        {
            BeforePresent?.Invoke(frame);
            PresentedFrames.Enqueue(frame);
            return PresentFailureReason is null;
        }

        public void CleanupResources()
        {
            CleanupCount++;
            IsInitialized = false;
            // A new device is a healthy one.
            DeviceLost = false;
        }

        public bool EnsureNativeFrameDevice(VideoFrameEventArgs frame, out bool deviceAdopted)
        {
            deviceAdopted = false;
            return AcceptsNativeFrames;
        }

        public void RaiseFramePresented() => FramePresented?.Invoke(this, EventArgs.Empty);
        public void Dispose() => IsInitialized = false;
    }
}
