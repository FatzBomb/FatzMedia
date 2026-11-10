using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Threading;
using FatzMedia.Video.Linux.Runtime;
using FatzMedia.Video.Linux.Threading;
using FatzMedia.Video.Linux.Video;
using Microsoft.Extensions.DependencyInjection;

namespace FatzMedia.Video.Linux;

/// <summary>
/// Registers Linux platform services into the Core composition graph.
/// </summary>
sealed class LinuxPlatformServices(LinuxNativeLibraryBootstrapper bootstrapper, LinuxPlatformOptions options) : IPlatformServices
{
    public void EnsureNativeLibrariesLoaded(IReadOnlyList<string> nativeLibraryPaths)
    {
        // FFmpeg is required.
        var result = bootstrapper.EnsureInitialized(nativeLibraryPaths);
        if (!result.IsInitialized)
            throw new InvalidOperationException(result.Message);
    }

    public void AddPlatformServices(IServiceCollection services, MediaPlayerConfiguration configuration)
    {
        _ = services.AddSingleton<IPrecisionWaiterFactory>(_ => new LinuxPrecisionWaiterFactory());
        _ = services.AddSingleton<IVideoPipelinePlatformServices>(_ => new LinuxVideoPipelinePlatformServices(options));
        _ = services.AddSingleton<IVideoHardwareAccelerationServices>(_ => SoftwareOnlyVideoHardwareAccelerationServices.Instance);
    }
}
