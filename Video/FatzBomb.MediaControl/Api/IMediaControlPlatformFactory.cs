using FatzBomb.MediaControl.Core.Configuration;

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
    /// Creates a service provider containing the platform implementation of the
    /// media engine and its supporting services for one player session.
    /// </summary>
    IServiceProvider CreateServiceProvider(
        MediaPlayerConfiguration configuration,
        IReadOnlyList<string> nativeLibraryPaths);
}
