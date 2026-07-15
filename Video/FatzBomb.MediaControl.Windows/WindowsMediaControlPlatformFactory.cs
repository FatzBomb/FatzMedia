using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Windows.Graphics;
using FatzBomb.MediaControl.Controllers;

namespace FatzBomb.MediaControl;

/// <summary>
/// Windows platform composition selected explicitly by Windows adapters and hosts
/// through the <see cref="IMediaControlPlatformFactory"/> seam.
/// </summary>
public sealed class WindowsMediaControlPlatformFactory : IMediaControlPlatformFactory
{
    /// <inheritdoc />
    public string LastNativeLoadMessage => Bootstrap.Message;

    /// <inheritdoc />
    public IMediaPresentationCoordinator CreatePresentationCoordinator(
        IMediaEngine mediaEngine,
        MediaPlayerConfiguration configuration) =>
        new NativeMediaPresentationCoordinator(mediaEngine, configuration);

    /// <inheritdoc />
    public IServiceProvider CreateServiceProvider(
        MediaPlayerConfiguration configuration,
        IReadOnlyList<string> nativeLibraryPaths) =>
        MediaPlayerServiceProvider.Create(configuration, nativeLibraryPaths);
}
