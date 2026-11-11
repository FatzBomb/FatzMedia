using FatzMedia.Video.Adapters.Archives;
using FatzMedia.Video.Audio;
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
        // Background mode (043A) never opens audio output; without an engine (ENG-033) no mode can.
        var (audioEngine, audioProblem) = MediaPlayerModePolicy.UsesAudioOutput(configuration.Mode)
            ? TryCreateAudioEngine(configuration)
            : (null, string.Empty);
        var audioAvailable = audioEngine is not null;

        var services = new ServiceCollection();
        _ = services.AddSingleton(new AudioEngineStartup(audioProblem));
        RegisterCoreServices(services, audioEngine);
        platformServices.AddPlatformServices(services, configuration);
        if (audioEngine is IAudioDeviceChangeSource deviceEvents)
            _ = services.AddSingleton<IAudioDeviceMonitor>(_ => new AudioEngineDeviceMonitor(deviceEvents));
        RegisterMediaEngine(services, configuration, audioAvailable);

        return services.BuildServiceProvider();
    }

    static void RegisterCoreServices(IServiceCollection services, IAudioEngine? audioEngine)
    {
        // Registered through a factory so the container disposes the engine.
        _ = services.AddSingleton(_ => audioEngine ?? new SilentAudioEngine());
        _ = services.AddSingleton<IMediaFileAnalyzer>(provider =>
            provider.GetRequiredService<IAudioEngine>() as IMediaFileAnalyzer ?? UnavailableMediaFileAnalyzer.Instance);
        // An engine that brings its own CD+G drawing (FatzAudio) supplies the CD+G engine.
        _ = services.AddSingleton(provider => provider.GetRequiredService<IAudioEngine>() is ICdgEngineSource source
            ? source.CreateCdgEngine()
            : new CdgEngine());
        _ = services.AddSingleton<ISubtitleEngine, SubtitleEngine>();
        _ = services.AddSingleton<IArchiveService, SharpArchiveAdapter>();
        _ = services.AddSingleton<IMidiLyricProvider, MidiLyricProvider>();
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
    /// The configured factory's engine, else the built-in FatzAudio engine; null, with a warning (also kept as the
    /// problem text, see <see cref="AudioEngineStartup"/>), when it cannot start: the player then has no audio.
    /// </summary>
    static (IAudioEngine? Engine, string Problem) TryCreateAudioEngine(MediaPlayerConfiguration configuration)
    {
        var factory = configuration.AudioEngineFactory ?? new FatzAudioEngineFactory();
        try
        {
            var engine = factory.Create(configuration)
                ?? throw new InvalidOperationException("The factory returned no engine.");
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "Bootstrap", $"Audio engine: {factory.Name}");
            return (engine, string.Empty);
        }
        catch (Exception ex)
        {
            var problem = $"Audio engine {factory.Name} could not start ({ex.Message}). {MediaPlayerModePolicy.AudioUnavailableMessage}: video plays without sound; audio media will not open.";
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Bootstrap", problem, innerException: ex);
            return (null, problem);
        }
    }
}

/// <summary>
/// Why the player's audio engine did not start (empty when it started, or in Background mode). It is raised while the
/// player is being built, before a host can subscribe to its diagnostics, so <see cref="PlayerCore.AudioEngineProblem"/>
/// keeps it.
/// </summary>
sealed record AudioEngineStartup(string Problem);
