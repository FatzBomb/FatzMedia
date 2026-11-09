using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Linux.Runtime;
using FatzMedia.Video.Linux.Video;

namespace FatzMedia.Video.Linux;

/// <summary>
/// Linux platform composition. Hosts choose the renderer independently by passing an
/// <see cref="IMediaPresentationCoordinatorFactory"/> (for example the portable software
/// renderer), exactly like the Windows factory.
/// </summary>
public sealed class LinuxPlatformFactory : IPlatformFactory
{
    readonly IMediaPresentationCoordinatorFactory _presentationFactory;
    readonly LinuxPlatformOptions _options;
    readonly LinuxNativeLibraryBootstrapper _bootstrapper;

    public LinuxPlatformFactory(IMediaPresentationCoordinatorFactory presentationFactory, LinuxPlatformOptions? options = null)
        : this(presentationFactory, options, LinuxNativeLibraryBootstrapper.Default)
    {
    }

    internal LinuxPlatformFactory(
        IMediaPresentationCoordinatorFactory presentationFactory,
        LinuxPlatformOptions? options,
        LinuxNativeLibraryBootstrapper bootstrapper)
    {
        _presentationFactory = presentationFactory ?? throw new ArgumentNullException(nameof(presentationFactory));
        _options = options ?? LinuxPlatformOptions.Default;
        _bootstrapper = bootstrapper ?? throw new ArgumentNullException(nameof(bootstrapper));
    }

    /// <inheritdoc />
    public string LastNativeLoadMessage => _bootstrapper.LastResult?.Message ?? string.Empty;

    public NativeDependencyReport NativeDependencies => _bootstrapper.LastResult?.Report ?? NativeDependencyReport.Empty;

    /// <inheritdoc />
    public VideoDecodeCapabilities GetVideoDecodeCapabilities() =>
        LinuxVideoDecodeCapabilities.GetOrScan(_bootstrapper.LastResult?.IsFfmpegLoaded == true);

    /// <inheritdoc />
    public IMediaPresentationCoordinator CreatePresentationCoordinator(IMediaEngine mediaEngine, MediaPlayerConfiguration configuration) =>
        _presentationFactory.Create(mediaEngine, configuration);

    /// <inheritdoc />
    public IServiceProvider CreateServiceProvider(MediaPlayerConfiguration configuration, IReadOnlyList<string> nativeLibraryPaths) =>
        PlayerServiceProvider.Create(configuration, nativeLibraryPaths, new LinuxPlatformServices(_bootstrapper, _options));
}
