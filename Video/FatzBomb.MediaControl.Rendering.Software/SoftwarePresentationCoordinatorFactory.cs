using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Threading;
using FatzBomb.MediaControl.Rendering.Text;

namespace FatzBomb.MediaControl.Rendering.Software;

/// <summary>Creates a portable Core presentation coordinator backed by software composition.</summary>
public sealed class SoftwarePresentationCoordinatorFactory : IMediaPresentationCoordinatorFactory, IMediaPresentationAvailabilityProbe
{
    private readonly IPrecisionWaiterFactory _precisionWaiterFactory;

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
    /// Creates the coordinator. Plain subtitles are drawn by
    /// <see cref="SkiaSubtitleRasterizer"/> with the configuration's subtitle style. Karaoke lyrics
    /// are not drawn by this renderer yet (FatzAudio's lyric surface will provide them).
    /// </summary>
    public IMediaPresentationCoordinator Create(
        IMediaEngine mediaEngine,
        MediaPlayerConfiguration configuration) => new MediaPresentationCoordinator(
            new SoftwareRendererBackend(),
            new SkiaSubtitleRasterizer(configuration.SubtitleStyle),
            mediaEngine,
            configuration,
            _precisionWaiterFactory);
}
