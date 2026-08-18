using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
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
        public bool PresentNativeFrame(VideoFrameEventArgs frame) => false;
        public void ClearCompositionBackgroundFrame() { }

        public bool PresentComposition(MediaCompositionFrame frame)
        {
            PresentedFrames.Enqueue(frame);
            return true;
        }

        public void CleanupResources()
        {
            CleanupCount++;
            IsInitialized = false;
        }

        public bool EnsureNativeFrameDevice(VideoFrameEventArgs frame, out bool deviceAdopted)
        {
            deviceAdopted = false;
            return false;
        }

        public void RaiseFramePresented() => FramePresented?.Invoke(this, EventArgs.Empty);
        public void Dispose() => IsInitialized = false;
    }
}
