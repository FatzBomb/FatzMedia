using FatzMedia.Video.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FatzMedia.Video.Tests;

public sealed class PlayerServiceProviderTests
{
    [Fact]
    public void Create_InitializesAndRegistersPlatformServices()
    {
        var platformServices = new TestPlatformServices();

        var serviceProvider = PlayerServiceProvider.Create(
            new MediaPlayerConfiguration(),
            ["native-a", "native-b"],
            platformServices);

        Assert.Equal(["native-a", "native-b"], platformServices.NativeLibraryPaths);
        Assert.True(platformServices.PlatformServicesRegistered);
        Assert.NotNull(serviceProvider);
    }

    private sealed class TestPlatformServices : IPlatformServices
    {
        public IReadOnlyList<string> NativeLibraryPaths { get; private set; } = [];
        public bool PlatformServicesRegistered { get; private set; }

        public void EnsureNativeLibrariesLoaded(IReadOnlyList<string> nativeLibraryPaths) =>
            NativeLibraryPaths = nativeLibraryPaths;

        public void AddPlatformServices(IServiceCollection services, MediaPlayerConfiguration configuration) =>
            PlatformServicesRegistered = true;
    }
}
