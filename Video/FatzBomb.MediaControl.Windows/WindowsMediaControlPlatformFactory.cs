using FatzBomb.MediaControl.Core.Configuration;

namespace FatzBomb.MediaControl;

/// <summary>
/// Windows platform composition selected explicitly by Windows adapters and hosts
/// through the <see cref="IMediaControlPlatformFactory"/> seam.
/// </summary>
public sealed class WindowsMediaControlPlatformFactory : IMediaControlPlatformFactory
{
    /// <inheritdoc />
    public IServiceProvider CreateServiceProvider(
        MediaPlayerConfiguration configuration,
        IReadOnlyList<string> nativeLibraryPaths) =>
        MediaPlayerServiceProvider.Create(configuration, nativeLibraryPaths);
}
