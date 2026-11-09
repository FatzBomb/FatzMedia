using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Threading;
using FatzMedia.Video.Windows.Audio;
using FatzMedia.Video.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace FatzMedia.Video;

/// <summary>
/// Registers Windows-native media services into the Core composition graph.
/// </summary>
sealed class WindowsPlatformServices : IPlatformServices
{
    /// <summary>FFmpeg is required; without BASS the player starts with no audio (ENG-033).</summary>
    public void EnsureNativeLibrariesLoaded(IReadOnlyList<string> nativeLibraryPaths)
    {
        if (!Bootstrap.EnsureInitialized(nativeLibraryPaths))
        {
            throw new InvalidOperationException(Bootstrap.Message);
        }
    }

    public bool IsAudioAvailable => Bootstrap.IsBassLoaded;

    public void AddPlatformServices(IServiceCollection services, MediaPlayerConfiguration configuration)
    {
        _ = services.AddSingleton<IPrecisionWaiterFactory>(_ => new WindowsPrecisionWaiterFactory());
        _ = services.AddSingleton<IVideoPipelinePlatformServices>(_ => new WindowsVideoPipelinePlatformServices());
        _ = services.AddSingleton<IVideoHardwareAccelerationServices>(_ => new WindowsVideoHardwareAccelerationServices());
        // The monitor polls BASS, so it exists only when BASS loaded.
        if (IsAudioAvailable)
            _ = services.AddSingleton<IAudioDeviceMonitor>(_ => new PollingAudioDeviceMonitor());
    }
}
