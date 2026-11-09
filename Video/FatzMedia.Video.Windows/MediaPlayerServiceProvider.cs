using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video;

/// <summary>
/// Windows service provider for media-player dependency injection.
/// Creates Windows-native media-engine services based on MediaPlayerConfiguration.
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
        => PlayerServiceProvider.Create(configuration, nativeLibraryPaths, new WindowsPlatformServices());

    private static IReadOnlyList<string> ToNativeLibraryPathArrayOrDefault(string? nativeLibraryPath) =>
        string.IsNullOrWhiteSpace(nativeLibraryPath)
            ? DLLPath.GetConfiguredNativeLibraryPaths()
            : [nativeLibraryPath];
}
