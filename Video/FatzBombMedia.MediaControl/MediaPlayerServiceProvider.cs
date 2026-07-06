using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

namespace FatzBombMedia.MediaControl;

/// <summary>
/// Service provider for MediaPlayer dependency injection.
/// Creates optimized service configurations based on MediaPlayerConfiguration.
/// </summary>
public static class MediaPlayerServiceProvider
{
    /// <summary>
    /// Creates a service provider with default configuration.
    /// </summary>
    public static IServiceProvider Create() => Create(MediaPlayerConfiguration.Auto, DLLPath.GetConfiguredNativeLibraryPaths());

    /// <summary>
    /// Creates a service provider with the specified configuration.
    /// </summary>
    public static IServiceProvider Create(MediaPlayerConfiguration configuration) => Create(configuration, DLLPath.GetConfiguredNativeLibraryPaths());

    /// <summary>
    /// Creates a service provider with specified configuration and optional library path.
    /// </summary>
    public static IServiceProvider Create(MediaPlayerConfiguration configuration, string? userLibraryPath = null)
        => Create(configuration, ToNativeLibraryPathArrayOrDefault(userLibraryPath));

    /// <summary>
    /// Creates a service provider with specified configuration and host-provided native library paths.
    /// </summary>
    public static IServiceProvider Create(MediaPlayerConfiguration configuration, IReadOnlyList<string> nativeLibraryPaths)
    {
        var services = new ServiceCollection();

        RegisterServices(services, configuration, nativeLibraryPaths);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Registers all required services for MediaPlayer functionality.
    /// </summary>
    private static void RegisterServices(IServiceCollection services, MediaPlayerConfiguration configuration, IReadOnlyList<string> nativeLibraryPaths)
    {

        // Load native libraries only once globally - pass custom path if provided
        _ = LoadLibraries(nativeLibraryPaths);

        _ = services.AddSingleton<IAudioEngine>(provider =>
        {
            var audioEngine = new Core.Audio.AudioEngine();

            if (!audioEngine.Init(-1, 44100))
            {
                Debug.WriteLine("[MediaPlayerServiceProvider] ❌ Failed to initialize global BASS");
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "Bootstrap", "Failed to initialize global BASS");
            }
            else
            {
                Debug.WriteLine("[MediaPlayerServiceProvider] ✅ Global BASS initialized successfully");
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "Bootstrap", "Global BASS initialized successfully");
            }

            Debug.WriteLine("[MediaPlayerServiceProvider] Created AudioEngine for pipeline audio slice");
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "Bootstrap", "AudioEngine created for pipeline audio slice");
            return audioEngine;
        });

        _ = services.AddSingleton<ICdgEngine, Core.Cdg.CdgEngine>();
        _ = services.AddSingleton<IArchiveService, Adapters.Archives.SharpArchiveAdapter>();

        _ = services.AddSingleton<IMediaEngine>(provider =>
        {
            IAudioEngine audioEngine = provider.GetRequiredService<IAudioEngine>();
            ICdgEngine cdgEngine = provider.GetRequiredService<ICdgEngine>();
            IArchiveService archiveService = provider.GetRequiredService<IArchiveService>();
            return new PipelineMediaEngine(audioEngine, cdgEngine, archiveService, configuration);
        });
    }

    private static bool LoadLibraries(IReadOnlyList<string> nativeLibraryPaths) =>
        !Bootstrap.EnsureInitialized(nativeLibraryPaths)
            ? throw new InvalidOperationException(Bootstrap.Message)
            : true;

    private static IReadOnlyList<string> ToNativeLibraryPathArrayOrDefault(string? nativeLibraryPath) =>
        string.IsNullOrWhiteSpace(nativeLibraryPath)
            ? DLLPath.GetConfiguredNativeLibraryPaths()
            : [nativeLibraryPath];
}
