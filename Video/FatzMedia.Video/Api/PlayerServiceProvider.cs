using FatzMedia.Video.Adapters.Archives;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Audio;
using FatzMedia.Video.Core.Cdg;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Subtitle;
using FatzMedia.Video.Core.Threading;
using FatzMedia.Video.Core.Video;
using FatzMedia.Video.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace FatzMedia.Video;

/// <summary>
/// Creates the Core media-service graph using services supplied by the selected platform.
/// </summary>
public static class PlayerServiceProvider
{
    /// <summary>
    /// Creates a media-service provider for a platform implementation.
    /// </summary>
    public static IServiceProvider Create(
        MediaPlayerConfiguration configuration,
        IReadOnlyList<string> nativeLibraryPaths,
        IPlatformServices platformServices)
    {
        ArgumentNullException.ThrowIfNull(platformServices);
        platformServices.EnsureNativeLibrariesLoaded(nativeLibraryPaths);
        var usesAudio = MediaPlayerModePolicy.UsesAudioOutput(configuration.Mode);
        // An engine from a factory (FatzAudio) plays everything, video audio included (037A slice 5):
        // it needs no BASS, and BASS is only its fallback.
        var factoryEngine = usesAudio ? TryCreateFactoryEngine(configuration, platformServices.IsAudioAvailable) : null;
        var audioAvailable = factoryEngine is not null || platformServices.IsAudioAvailable;
        if (!audioAvailable && usesAudio)
        {
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Warning,
                "Bootstrap",
                $"{MediaPlayerModePolicy.AudioUnavailableMessage}. Video plays without sound; audio media will not open.");
        }

        var services = new ServiceCollection();
        RegisterCoreServices(services, configuration, audioAvailable, factoryEngine);
        platformServices.AddPlatformServices(services, configuration);
        if (factoryEngine is not null)
            RegisterEngineDeviceMonitor(services);
        RegisterMediaEngine(services, configuration, audioAvailable);

        return services.BuildServiceProvider();
    }

    static void RegisterCoreServices(IServiceCollection services, MediaPlayerConfiguration configuration, bool audioAvailable, IAudioEngine? factoryEngine)
    {
        _ = services.AddSingleton(MediaAnalysisCache.Shared);
        // Background mode (043A) never initializes audio output; without an engine (ENG-033) no mode can.
        // Registered through a factory so the container disposes the engine.
        _ = services.AddSingleton(provider => factoryEngine
            ?? (audioAvailable && MediaPlayerModePolicy.UsesAudioOutput(configuration.Mode)
                ? CreateBassAudioEngine(configuration, provider.GetRequiredService<MediaAnalysisCache>())
                : new SilentAudioEngine()));
        _ = services.AddSingleton<IMediaFileAnalyzer>(provider =>
            provider.GetRequiredService<IAudioEngine>() as IMediaFileAnalyzer ?? UnavailableMediaFileAnalyzer.Instance);
        // An engine that brings its own CD+G drawing (FatzAudio, 037A slice 3) supplies the CD+G engine.
        _ = services.AddSingleton(provider => provider.GetRequiredService<IAudioEngine>() is ICdgEngineSource source
            ? source.CreateCdgEngine()
            : new CdgEngine());
        _ = services.AddSingleton<ISubtitleEngine, SubtitleEngine>();
        _ = services.AddSingleton<IArchiveService, SharpArchiveAdapter>();
        _ = services.AddSingleton<IMidiLyricProvider, BassMidiLyricProvider>();
    }

    static void RegisterMediaEngine(IServiceCollection services, MediaPlayerConfiguration configuration, bool audioAvailable)
    {
        // Every decoder of the player uses the platform's hardware-decode services, unless the
        // presentation renderer decodes on its own device (Stage 045A slice 2): the coordinator
        // attaches it through the engine.
        _ = services.AddSingleton(provider =>
            new RendererVideoDecodeServices(provider.GetRequiredService<IVideoHardwareAccelerationServices>()));
        _ = services.AddSingleton<IVideoEngineFactory>(provider =>
            new VideoPipelineFactory(
                provider.GetRequiredService<IPrecisionWaiterFactory>(),
                provider.GetRequiredService<IVideoPipelinePlatformServices>(),
                provider.GetRequiredService<RendererVideoDecodeServices>(),
                audioAvailable));

        _ = services.AddSingleton<IMediaEngine>(provider =>
            new PipelineMediaEngine(
                provider.GetRequiredService<IAudioEngine>(),
                provider.GetRequiredService<ICdgEngine>(),
                provider.GetRequiredService<ISubtitleEngine>(),
                provider.GetRequiredService<IArchiveService>(),
                provider.GetRequiredService<IVideoEngineFactory>(),
                provider.GetRequiredService<IMidiLyricProvider>(),
                provider.GetRequiredService<IPrecisionWaiterFactory>(),
                provider.GetRequiredService<RendererVideoDecodeServices>(),
                configuration,
                audioAvailable: audioAvailable));
    }

    /// <summary>
    /// The configured factory's engine; null without a factory, or when it fails (a warning says whether
    /// BASS takes over).
    /// </summary>
    static IAudioEngine? TryCreateFactoryEngine(MediaPlayerConfiguration configuration, bool bassAvailable)
    {
        if (configuration.AudioEngineFactory is not { } factory)
            return null;

        try
        {
            var engine = factory.Create(configuration)
                ?? throw new InvalidOperationException("The factory returned no engine.");
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "Bootstrap", $"Audio engine: {factory.Name} (files, video audio and CD+G; BASS is not used)");
            return engine;
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Warning,
                "Bootstrap",
                $"Audio engine {factory.Name} could not start ({ex.Message}); {(bassAvailable ? "using BASS." : "no audio.")}",
                innerException: ex);
            return null;
        }
    }

    /// <summary>
    /// With an <see cref="IAudioEngineFactory"/> engine, the device monitor also reports that
    /// engine's own device events (<see cref="IAudioDeviceChangeSource"/>), with the platform's monitor
    /// (it watches the device list through BASS, when BASS loaded) unless the engine watches the list itself.
    /// </summary>
    static void RegisterEngineDeviceMonitor(IServiceCollection services)
    {
        var platformMonitor = services.LastOrDefault(static descriptor => descriptor.ServiceType == typeof(IAudioDeviceMonitor));
        _ = services.AddSingleton<IAudioDeviceMonitor>(provider =>
        {
            var engine = provider.GetRequiredService<IAudioEngine>() as IAudioDeviceChangeSource;
            var platform = engine is { ReportsDeviceList: true } ? null : platformMonitor switch
            {
                { ImplementationFactory: { } create } => create(provider) as IAudioDeviceMonitor,
                { ImplementationInstance: IAudioDeviceMonitor instance } => instance,
                { ImplementationType: { } type } => ActivatorUtilities.CreateInstance(provider, type) as IAudioDeviceMonitor,
                _ => null,
            };
            return new AudioEngineDeviceMonitor(engine, platform);
        });
    }

    static AudioEngine CreateBassAudioEngine(
        MediaPlayerConfiguration configuration,
        MediaAnalysisCache analysisCache)
    {
        var latencyProvider = new BassOutputLatencyProvider(configuration.EnableUsbBluetoothLatencyHeuristic);
        var audioEngine = new AudioEngine(
            new AudioNormalizer(),
            new AudioStreamManager(),
            latencyProvider,
            configuration.SilenceTrim,
            analysisCache,
            configuration.NetworkRetry);
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
