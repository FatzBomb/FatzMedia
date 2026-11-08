using FatzBomb.MediaControl.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FatzBomb.MediaControl.Tests;

public sealed class MediaControlServiceProviderTests
{
    [Fact]
    public void Create_InitializesAndRegistersPlatformServices()
    {
        var platformServices = new TestPlatformServices();

        var serviceProvider = MediaControlServiceProvider.Create(
            new MediaPlayerConfiguration(),
            ["native-a", "native-b"],
            platformServices);

        Assert.Equal(["native-a", "native-b"], platformServices.NativeLibraryPaths);
        Assert.True(platformServices.PlatformServicesRegistered);
        Assert.NotNull(serviceProvider);
    }

    private sealed class TestPlatformServices : IMediaControlPlatformServices
    {
        public IReadOnlyList<string> NativeLibraryPaths { get; private set; } = [];
        public bool PlatformServicesRegistered { get; private set; }

        public void EnsureNativeLibrariesLoaded(IReadOnlyList<string> nativeLibraryPaths) =>
            NativeLibraryPaths = nativeLibraryPaths;

        public void AddPlatformServices(IServiceCollection services, MediaPlayerConfiguration configuration) =>
            PlatformServicesRegistered = true;
    }
}
