using FatzBomb.MediaControl.Adapters.Archives;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Core.Cdg;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Subtitle;
using FatzBomb.MediaControl.Core.Threading;
using FatzBomb.MediaControl.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace FatzBomb.MediaControl;

/// <summary>
/// Creates the Core media-service graph using services supplied by the selected platform.
/// </summary>
public static class MediaControlServiceProvider
{
    /// <summary>
    /// Creates a media-service provider for a platform implementation.
    /// </summary>
    public static IServiceProvider Create(
        MediaPlayerConfiguration configuration,
        IReadOnlyList<string> nativeLibraryPaths,
        IMediaControlPlatformServices platformServices)
    {
        ArgumentNullException.ThrowIfNull(platformServices);
        platformServices.EnsureNativeLibrariesLoaded(nativeLibraryPaths);

        var services = new ServiceCollection();
        RegisterCoreServices(services, configuration);
        platformServices.AddPlatformServices(services, configuration);
        RegisterMediaEngine(services, configuration);

        return services.BuildServiceProvider();
    }

    private static void RegisterCoreServices(IServiceCollection services, MediaPlayerConfiguration configuration)
    {
        _ = services.AddSingleton<IAudioEngine>(_ => CreateAudioEngine(configuration));
        _ = services.AddSingleton<ICdgEngine, CdgEngine>();
        _ = services.AddSingleton<ISubtitleEngine, SubtitleEngine>();
        _ = services.AddSingleton<IArchiveService, SharpArchiveAdapter>();
    }

    private static void RegisterMediaEngine(IServiceCollection services, MediaPlayerConfiguration configuration)
    {
        _ = services.AddSingleton<IMediaEngine>(provider =>
            new PipelineMediaEngine(
                provider.GetRequiredService<IAudioEngine>(),
                provider.GetRequiredService<ICdgEngine>(),
                provider.GetRequiredService<ISubtitleEngine>(),
                provider.GetRequiredService<IArchiveService>(),
                provider.GetRequiredService<IVideoEngineFactory>(),
                provider.GetRequiredService<IMidiLyricProvider>(),
                provider.GetRequiredService<IPrecisionWaiterFactory>(),
                configuration));
    }

    private static IAudioEngine CreateAudioEngine(MediaPlayerConfiguration configuration)
    {
        var latencyProvider = new BassOutputLatencyProvider(configuration.EnableUsbBluetoothLatencyHeuristic);
        var audioEngine = new AudioEngine(new AudioNormalizer(), new AudioStreamManager(), latencyProvider);
        var requestedDevice = configuration.AudioOutputDeviceIndex;
        var initialized = audioEngine.Init(requestedDevice, 44100);

        if (!initialized
            && requestedDevice != AudioOutputDeviceInfo.PrimaryDeviceIndex
            && configuration.AudioOutputFallbackMode == AudioOutputFallbackMode.FallbackToPrimary)
        {
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Warning,
                "Bootstrap",
                $"Audio output device {requestedDevice} could not be initialized ({ManagedBass.Bass.LastError}); falling back to Primary.");
            initialized = audioEngine.Init(AudioOutputDeviceInfo.PrimaryDeviceIndex, 44100);
        }

        if (!initialized)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "Bootstrap", $"Failed to initialize BASS audio output device {requestedDevice}");
        }
        else
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "Bootstrap", $"Global BASS initialized successfully on device {audioEngine.BassDeviceIndex}");
            BassMidiSoundfontManager.Initialize(configuration.MidiSoundfontPath);
        }

        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "Bootstrap", "AudioEngine created for pipeline audio slice");
        return audioEngine;
    }
}
