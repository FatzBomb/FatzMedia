using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Threading;
using FatzBomb.MediaControl.Windows.Audio;
using FatzBomb.MediaControl.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace FatzBomb.MediaControl;

/// <summary>
/// Registers Windows-native media services into the Core composition graph.
/// </summary>
internal sealed class WindowsMediaControlPlatformServices : IMediaControlPlatformServices
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
