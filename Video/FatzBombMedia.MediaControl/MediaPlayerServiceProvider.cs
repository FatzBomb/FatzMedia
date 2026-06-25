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
        LoadLibraries(nativeLibraryPaths);

        services.AddSingleton<IAudioEngine>(provider =>
        {
            var audioEngine = new Core.Audio.AudioEngine();
            
            if (!audioEngine.Init(-1, 44100))
                Debug.WriteLine("[MediaPlayerServiceProvider] ❌ Failed to initialize global BASS");
            else
                Debug.WriteLine("[MediaPlayerServiceProvider] ✅ Global BASS initialized successfully");
            
            Debug.WriteLine("[MediaPlayerServiceProvider] Created AudioEngine for pipeline audio slice");
            return audioEngine;
        });

        services.AddSingleton<ICdgEngine, Core.Cdg.CdgEngine>();
        services.AddSingleton<IArchiveService, Adapters.Archives.SharpArchiveAdapter>();
        
        services.AddSingleton<IMediaEngine>(provider =>
        {
            var audioEngine = provider.GetRequiredService<IAudioEngine>();
            var cdgEngine = provider.GetRequiredService<ICdgEngine>();
            var archiveService = provider.GetRequiredService<IArchiveService>();
            return new PipelineMediaEngine(audioEngine, cdgEngine, archiveService, configuration);
        });
    }
    
    private static bool LoadLibraries(IReadOnlyList<string> nativeLibraryPaths)
    {
        if (!Bootstrap.EnsureInitialized(nativeLibraryPaths))
            throw new InvalidOperationException(Bootstrap.Message);

        return true;
    }

    private static IReadOnlyList<string> ToNativeLibraryPathArrayOrDefault(string? nativeLibraryPath)
    {
        return string.IsNullOrWhiteSpace(nativeLibraryPath)
            ? DLLPath.GetConfiguredNativeLibraryPaths()
            : [nativeLibraryPath];
    }
}
