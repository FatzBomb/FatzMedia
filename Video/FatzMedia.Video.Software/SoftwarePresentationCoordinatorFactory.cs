using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Threading;
using FatzMedia.Video.Text;

namespace FatzMedia.Video.Software;

/// <summary>Creates a portable Core presentation coordinator backed by software composition.</summary>
public sealed class SoftwarePresentationCoordinatorFactory : IMediaPresentationCoordinatorFactory, IMediaPresentationAvailabilityProbe
{
    readonly IPrecisionWaiterFactory _precisionWaiterFactory;

    public SoftwarePresentationCoordinatorFactory()
        : this(new SystemPrecisionWaiterFactory())
    {
    }

    public SoftwarePresentationCoordinatorFactory(IPrecisionWaiterFactory precisionWaiterFactory)
    {
        _precisionWaiterFactory = precisionWaiterFactory
            ?? throw new ArgumentNullException(nameof(precisionWaiterFactory));
    }

    public string BackendName => "Software";

    /// <summary>
    /// Always available (Stage 036A). It draws only into managed frame-sink surfaces and presents
    /// only CPU frames, so hosts pair it with such a surface and software decoding.
    /// </summary>
    public MediaPresentationAvailability ProbeAvailability() =>
        MediaPresentationAvailability.Available("CPU composition") with
        {
            RequiresSoftwareDecoding = true,
            RequiresFrameSinkSurface = true
        };

    /// <summary>
    /// Creates the coordinator. Subtitles and karaoke lyrics are drawn by
    /// <see cref="SkiaSubtitleRasterizer"/> with the configuration's subtitle and karaoke styles.
    /// </summary>
    public IMediaPresentationCoordinator Create(
        IMediaEngine mediaEngine,
        MediaPlayerConfiguration configuration) => new MediaPresentationCoordinator(
            new SoftwareRendererBackend(),
            new SkiaSubtitleRasterizer(configuration.SubtitleStyle, configuration.KaraokeStyle),
            mediaEngine,
            configuration,
            _precisionWaiterFactory);
}
