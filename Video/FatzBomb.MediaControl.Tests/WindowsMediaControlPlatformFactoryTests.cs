using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;

namespace FatzBomb.MediaControl.Tests;

public sealed class WindowsMediaControlPlatformFactoryTests
{
    [Fact]
    public void CreatePresentationCoordinator_UsesInjectedFactory()
    {
        var expectedCoordinator = new TestPresentationCoordinator();
        var rendererFactory = new TestPresentationCoordinatorFactory(expectedCoordinator);
        var platformFactory = new WindowsMediaControlPlatformFactory(rendererFactory);
        var configuration = new MediaPlayerConfiguration();

        var coordinator = platformFactory.CreatePresentationCoordinator(null!, configuration);

        Assert.Same(expectedCoordinator, coordinator);
        Assert.Equal(configuration, rendererFactory.Configuration);
        Assert.Null(rendererFactory.MediaEngine);
    }

    private sealed class TestPresentationCoordinatorFactory(IMediaPresentationCoordinator coordinator) : IMediaPresentationCoordinatorFactory
    {
        public string BackendName => "Test";

        public IMediaEngine? MediaEngine { get; private set; }
        public MediaPlayerConfiguration? Configuration { get; private set; }

        public IMediaPresentationCoordinator Create(IMediaEngine mediaEngine, MediaPlayerConfiguration configuration)
        {
            MediaEngine = mediaEngine;
            Configuration = configuration;
            return coordinator;
        }
    }

    private sealed class TestPresentationCoordinator : IMediaPresentationCoordinator
    {
        public event EventHandler? FramePresented
        {
            add { }
            remove { }
        }

        public VideoDisplayMode VideoDisplayMode { get; set; }

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
