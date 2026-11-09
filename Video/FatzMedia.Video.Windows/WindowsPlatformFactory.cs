using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Controllers;
using FatzMedia.Video.Windows.Video;

namespace FatzMedia.Video;

/// <summary>
/// Windows platform composition selected explicitly by Windows adapters and hosts
/// through the <see cref="IPlatformFactory"/> seam.
/// </summary>
public class WindowsPlatformFactory : IPlatformFactory
{
    private readonly IMediaPresentationCoordinatorFactory _presentationFactory;

    public WindowsPlatformFactory(IMediaPresentationCoordinatorFactory presentationFactory)
    {
        _presentationFactory = presentationFactory ?? throw new ArgumentNullException(nameof(presentationFactory));
    }

    /// <inheritdoc />
    public string LastNativeLoadMessage => Bootstrap.Message;

    public NativeDependencyReport NativeDependencies => Bootstrap.NativeDependencies;

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
