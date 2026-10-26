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
        var second = new Thread(() => coordinator.BaseVisualTransform = new BaseVisualTransform { Brightness = 0.5f });
        second.Start();

        // Without ordering the second composition is built and presented while the first waits.
        _ = second.Join(TimeSpan.FromMilliseconds(300));
        release.Set();
        first.Join();
        second.Join();

        Assert.Equal(
            ["current position refreshed", "base visual transform changed"],
            backend.PresentedFrames.Skip(baseline).Select(static frame => frame.Reason).ToArray());
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
        TestMediaEngine engine) => new(
            backend,
            new StubSubtitleRasterizer(),
            engine,
            MediaPlayerConfiguration.Auto,
            new SystemPrecisionWaiterFactory());

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

        public bool Initialize()
        {
            InitializeCount++;
            IsInitialized = true;
            return true;
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
