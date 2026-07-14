using FatzBomb.MediaControl.Core.Configuration;

namespace FatzBomb.MediaControl;

/// <summary>
/// Current Windows platform composition. This implementation remains in the Core
/// project only until the Windows engine project is created; Windows adapters and
/// hosts select it explicitly through the <see cref="IMediaControlPlatformFactory"/> seam.
/// </summary>
public sealed class WindowsMediaControlPlatformFactory : IMediaControlPlatformFactory
{
    /// <inheritdoc />
    public IServiceProvider CreateServiceProvider(
        MediaPlayerConfiguration configuration,
        IReadOnlyList<string> nativeLibraryPaths) =>
        MediaPlayerServiceProvider.Create(configuration, nativeLibraryPaths);
}
