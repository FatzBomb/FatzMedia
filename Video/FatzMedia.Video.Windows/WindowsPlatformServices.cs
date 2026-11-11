using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Threading;
using FatzMedia.Video.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace FatzMedia.Video;

/// <summary>
/// Registers Windows-native media services into the Core composition graph.
/// </summary>
sealed class WindowsPlatformServices : IPlatformServices
{
    /// <summary>FFmpeg is required.</summary>
    public void EnsureNativeLibrariesLoaded(IReadOnlyList<string> nativeLibraryPaths)
    {
        if (!Bootstrap.EnsureInitialized(nativeLibraryPaths))
        {
            throw new InvalidOperationException(Bootstrap.Message);
        }
    }

    public void AddPlatformServices(IServiceCollection services, MediaPlayerConfiguration configuration)
    {
        _ = services.AddSingleton<IPrecisionWaiterFactory>(_ => new WindowsPrecisionWaiterFactory());
        _ = services.AddSingleton<IVideoPipelinePlatformServices>(_ => new WindowsVideoPipelinePlatformServices());
        _ = services.AddSingleton<IVideoHardwareAccelerationServices>(_ => new WindowsVideoHardwareAccelerationServices());
    }
}
