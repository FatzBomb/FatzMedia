using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Tests;

public sealed class WindowsPlatformFactoryTests
{
    [Fact]
    public void CreatePresentationCoordinator_UsesInjectedFactory()
    {
        var expectedCoordinator = new NullPresentationCoordinator();
        var rendererFactory = new TestPresentationCoordinatorFactory(expectedCoordinator);
        var platformFactory = new WindowsPlatformFactory(rendererFactory);
        var configuration = new MediaPlayerConfiguration();

        var coordinator = platformFactory.CreatePresentationCoordinator(null!, configuration);

        Assert.Same(expectedCoordinator, coordinator);
        Assert.Equal(configuration, rendererFactory.Configuration);
        Assert.Null(rendererFactory.MediaEngine);
    }

    sealed class TestPresentationCoordinatorFactory(IMediaPresentationCoordinator coordinator) : IMediaPresentationCoordinatorFactory
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
}
