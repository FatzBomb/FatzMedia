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
    /// Whether the audio engine's native libraries (BASS) loaded. Read after
    /// <see cref="EnsureNativeLibrariesLoaded"/>. When false the player still starts: video plays
    /// without sound on the playback clock and media that needs audio does not open.
    /// </summary>
    bool IsAudioAvailable => true;

    /// <summary>
    /// Registers platform-specific media services, such as video decoding and device monitoring.
    /// </summary>
    void AddPlatformServices(IServiceCollection services, MediaPlayerConfiguration configuration);
}
