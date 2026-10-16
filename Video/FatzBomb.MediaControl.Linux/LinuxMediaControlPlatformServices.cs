using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Threading;
using FatzBomb.MediaControl.Linux.Audio;
using FatzBomb.MediaControl.Linux.Runtime;
using FatzBomb.MediaControl.Linux.Threading;
using FatzBomb.MediaControl.Linux.Video;
using Microsoft.Extensions.DependencyInjection;

namespace FatzBomb.MediaControl.Linux;

/// <summary>
/// Registers Linux platform services into the Core composition graph.
/// </summary>
internal sealed class LinuxMediaControlPlatformServices(LinuxNativeLibraryBootstrapper bootstrapper, LinuxPlatformOptions options) : IMediaControlPlatformServices
{
    public void EnsureNativeLibrariesLoaded(IReadOnlyList<string> nativeLibraryPaths)
    {
        LinuxNativeLoadResult result = bootstrapper.EnsureInitialized(nativeLibraryPaths);
        if (!result.IsInitialized)
            throw new InvalidOperationException(result.Message);
    }

    public void AddPlatformServices(IServiceCollection services, MediaPlayerConfiguration configuration)
    {
        _ = services.AddSingleton<IMidiLyricProvider>(_ => new LinuxBassMidiLyricProvider());
        _ = services.AddSingleton<IPrecisionWaiterFactory>(_ => new LinuxPrecisionWaiterFactory());
        _ = services.AddSingleton<IVideoPipelinePlatformServices>(_ => new LinuxVideoPipelinePlatformServices(options));
        _ = services.AddSingleton<IVideoHardwareAccelerationServices>(_ => SoftwareOnlyVideoHardwareAccelerationServices.Instance);
        _ = services.AddSingleton<IAudioDeviceMonitor>(_ => new BassDeviceAudioMonitor(new BassAudioOutputSnapshotSource(), options.AudioDevicePollInterval));
    }
}
