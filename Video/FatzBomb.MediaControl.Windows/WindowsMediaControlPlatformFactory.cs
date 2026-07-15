using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Rendering.Direct3D11;

namespace FatzBomb.MediaControl;

/// <summary>
/// Windows platform composition selected explicitly by Windows adapters and hosts
/// through the <see cref="IMediaControlPlatformFactory"/> seam.
/// </summary>
public sealed class WindowsMediaControlPlatformFactory : IMediaControlPlatformFactory
{
    private readonly IMediaPresentationCoordinatorFactory _presentationFactory;

    public WindowsMediaControlPlatformFactory()
        : this(new Direct3D11PresentationCoordinatorFactory())
    {
    }

    public WindowsMediaControlPlatformFactory(IMediaPresentationCoordinatorFactory presentationFactory)
    {
        _presentationFactory = presentationFactory ?? throw new ArgumentNullException(nameof(presentationFactory));
    }

    /// <inheritdoc />
    public string LastNativeLoadMessage => Bootstrap.Message;

    /// <inheritdoc />
    public IMediaPresentationCoordinator CreatePresentationCoordinator(
        IMediaEngine mediaEngine,
        MediaPlayerConfiguration configuration) =>
        _presentationFactory.Create(mediaEngine, configuration);

    /// <inheritdoc />
    public IServiceProvider CreateServiceProvider(
        MediaPlayerConfiguration configuration,
        IReadOnlyList<string> nativeLibraryPaths) =>
        MediaPlayerServiceProvider.Create(configuration, nativeLibraryPaths);
}
