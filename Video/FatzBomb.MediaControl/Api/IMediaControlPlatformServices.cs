using FatzBomb.MediaControl.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FatzBomb.MediaControl;

/// <summary>
/// Supplies operating-system-specific services required by Core media composition.
/// </summary>
public interface IMediaControlPlatformServices
{
    /// <summary>
    /// Loads platform-native media libraries before Core creates its media services.
    /// </summary>
    void EnsureNativeLibrariesLoaded(IReadOnlyList<string> nativeLibraryPaths);

    /// <summary>
    /// Registers platform-specific media services, such as video decoding and device monitoring.
    /// </summary>
    void AddPlatformServices(IServiceCollection services, MediaPlayerConfiguration configuration);
}
