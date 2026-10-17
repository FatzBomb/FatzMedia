using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Controllers;

#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>
/// Composes the platform-specific services required by a media-control session.
/// Core owns the session lifecycle and consumes the resulting services; each
/// operating-system package owns its native libraries, decoders, renderers, and
/// dependency registrations.
/// </summary>
public interface IMediaControlPlatformFactory
{
    /// <summary>
    /// Gets the most recent native-library initialization result for this platform.
    /// </summary>
    string LastNativeLoadMessage { get; }

    /// <summary>
    /// Gets the per-library result of the most recent native-library initialization. Platforms
    /// that do not check native libraries return <see cref="NativeDependencyReport.Empty"/>.
    /// </summary>
    NativeDependencyReport NativeDependencies => NativeDependencyReport.Empty;

    /// <summary>
    /// Gets codec/container decode capabilities for the current platform and
    /// available hardware backends.
    /// </summary>
    VideoDecodeCapabilities GetVideoDecodeCapabilities();

    /// <summary>
    /// Creates the platform presentation owner for a media session.
    /// </summary>
    IMediaPresentationCoordinator CreatePresentationCoordinator(
        IMediaEngine mediaEngine,
        MediaPlayerConfiguration configuration);

    /// <summary>
    /// Creates a service provider containing the platform implementation of the
    /// media engine and its supporting services for one player session.
    /// </summary>
    IServiceProvider CreateServiceProvider(
        MediaPlayerConfiguration configuration,
        IReadOnlyList<string> nativeLibraryPaths);
}
