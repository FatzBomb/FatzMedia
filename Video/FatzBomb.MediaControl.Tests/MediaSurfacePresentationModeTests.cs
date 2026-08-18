using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Wpf;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace FatzBomb.MediaControl.Tests;

public sealed class MediaSurfacePresentationModeTests
{
    [Fact]
    public void PresentationMode_StaysDeferredAcrossReplacementAndAppliesAfterClose()
    {
        Exception? threadException = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunDeferredPresentationModeScenario();
            }
            catch (Exception ex)
            {
                threadException = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA WPF test thread did not complete.");
        if (threadException != null)
            ExceptionDispatchInfo.Capture(threadException).Throw();
    }

    private static void RunDeferredPresentationModeScenario()
    {
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(
            new TestPlatformFactory(engine),
            MediaPlayerConfiguration.Auto,
            []);
        var surface = new MediaSurface { Player = player };

        Assert.True(player.Load("first.mp3"));
        surface.PresentationMode = WpfPresentationMode.WpfComposited;
        Assert.Equal(WpfPresentationMode.NativeHwndOverlay, surface.PresentationMode);

        Assert.True(player.Load("second.mp3"));
        DrainDispatcher();
        Assert.Equal(WpfPresentationMode.NativeHwndOverlay, surface.PresentationMode);

        player.Close();
        DrainDispatcher();
        Assert.Equal(WpfPresentationMode.WpfComposited, surface.PresentationMode);

        surface.Player = null;
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class TestPlatformFactory(TestMediaEngine engine) : IMediaControlPlatformFactory
    {
        private readonly TestPresentationCoordinator _coordinator = new();

        public string LastNativeLoadMessage => string.Empty;
        public VideoDecodeCapabilities GetVideoDecodeCapabilities() => VideoDecodeCapabilities.Create([], []);

        public IMediaPresentationCoordinator CreatePresentationCoordinator(
            IMediaEngine mediaEngine,
            MediaPlayerConfiguration configuration) => _coordinator;

        public IServiceProvider CreateServiceProvider(
            MediaPlayerConfiguration configuration,
            IReadOnlyList<string> nativeLibraryPaths) => new TestServiceProvider(engine);
    }

    private sealed class TestServiceProvider(TestMediaEngine engine) : IServiceProvider
    {
        private readonly IArchiveService _archiveService = new TestArchiveService();

        public object? GetService(Type serviceType) => serviceType == typeof(IMediaEngine)
            ? engine
            : serviceType == typeof(IArchiveService)
                ? _archiveService
                : null;
    }

    private sealed class TestArchiveService : IArchiveService
    {
        public IArchiveHandle Open(string path) => throw new NotSupportedException();
    }

    private sealed class TestPresentationCoordinator : IMediaPresentationCoordinator
    {
        public event EventHandler? FramePresented { add { } remove { } }
        public VideoDisplayMode VideoDisplayMode { get; set; }
        public BaseVisualTransform BaseVisualTransform { get; set; }
        public bool AttachPresentationSurface(MediaPresentationSurfaceDescriptor surface) => false;
        public bool ResizePresentationSurface(string surfaceId, int pixelWidth, int pixelHeight) => false;
        public bool SetPresentationSurfaceLiveResize(string surfaceId, bool isResizing) => false;
        public bool DetachPresentationSurface(string surfaceId) => false;
        public void RefreshCurrentPosition() { }
        public void RunOnRenderThread(Action action) => action();
        public void StartRendering() { }
        public void StopRendering() { }
        public void RefreshAttachments() { }
        public void Dispose() { }
    }
}
