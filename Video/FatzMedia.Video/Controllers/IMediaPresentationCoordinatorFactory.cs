using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Controllers;

/// <summary>
/// Creates a renderer-specific presentation coordinator. Platform composition
/// selects a factory; the factory owns renderer initialization and presentation.
/// </summary>
public interface IMediaPresentationCoordinatorFactory
{
    /// <summary>Gets the stable renderer/backend name shown in diagnostics and configuration.</summary>
    string BackendName { get; }

    /// <summary>Creates the renderer-specific presentation owner for one media session.</summary>
    IMediaPresentationCoordinator Create(
        IMediaEngine mediaEngine,
        MediaPlayerConfiguration configuration);
}
