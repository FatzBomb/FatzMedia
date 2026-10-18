using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Graphics;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Core.Subtitle;
using FatzBomb.MediaControl.Core.Threading;
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

        public bool AttachSurface(MediaPresentationSurfaceDescriptor surface)
        {
            AttachedSurfaces.Enqueue(surface);
            return true;
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
            NativeFramePresents++;
            return true;
        }
        public void ClearCompositionBackgroundFrame() { }

        public string? PresentFailureReason { get; set; }

        public string? LastPresentFailureReason => PresentFailureReason;

        public double? RefreshRateHz { get; set; }
        public MediaPresentationSurfaceDescriptor? LastRefreshRateSurface { get; private set; }

        public double? GetSurfaceRefreshRateHz(MediaPresentationSurfaceDescriptor surface)
        {
            LastRefreshRateSurface = surface;
            return RefreshRateHz;
        }

        public bool PresentComposition(MediaCompositionFrame frame)
        {
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
