using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Rendering.Direct3D11;
using FatzBomb.MediaControl.Windows.Video;
using FatzBomb.MediaControl.Windows.Threading;

namespace FatzBomb.MediaControl;

/// <summary>
/// Windows platform composition selected explicitly by Windows adapters and hosts
/// through the <see cref="IMediaControlPlatformFactory"/> seam.
/// </summary>
public sealed class WindowsMediaControlPlatformFactory : IMediaControlPlatformFactory
{
    private readonly IMediaPresentationCoordinatorFactory _presentationFactory;

    public WindowsMediaControlPlatformFactory()
        : this(new Direct3D11PresentationCoordinatorFactory(new WindowsPrecisionWaiterFactory()))
    {
    }

    public WindowsMediaControlPlatformFactory(IMediaPresentationCoordinatorFactory presentationFactory)
    {
        _presentationFactory = presentationFactory ?? throw new ArgumentNullException(nameof(presentationFactory));
    }

    /// <inheritdoc />
    public string LastNativeLoadMessage => Bootstrap.Message;

    /// <inheritdoc />
    public VideoDecodeCapabilities GetVideoDecodeCapabilities() =>
        WindowsVideoDecodeCapabilities.GetOrScan();

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
