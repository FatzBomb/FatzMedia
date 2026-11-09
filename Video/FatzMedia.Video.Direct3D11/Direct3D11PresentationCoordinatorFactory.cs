using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Graphics.D3D.Device;
using FatzMedia.Video.Core.Threading;
using FatzMedia.Video.Direct3D11.Subtitle;

namespace FatzMedia.Video.Direct3D11;

public sealed class Direct3D11PresentationCoordinatorFactory : IMediaPresentationCoordinatorFactory, IMediaPresentationAvailabilityProbe
{
    private readonly IPrecisionWaiterFactory _precisionWaiterFactory;
    private readonly Direct3D11PresentationOptions _options;

    public Direct3D11PresentationCoordinatorFactory(IPrecisionWaiterFactory precisionWaiterFactory)
        : this(precisionWaiterFactory, new Direct3D11PresentationOptions())
    {
    }

    public Direct3D11PresentationCoordinatorFactory(
        IPrecisionWaiterFactory precisionWaiterFactory,
        Direct3D11PresentationOptions options)
    {
        _precisionWaiterFactory = precisionWaiterFactory ?? throw new ArgumentNullException(nameof(precisionWaiterFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string BackendName => "Direct3D11";

    /// <summary>
    /// Creates and releases a device with the configured <see cref="Direct3D11PresentationOptions.DeviceSelection"/>
    /// (Stage 036A), so a host can fall back to another renderer when none can be created.
    /// </summary>
    public MediaPresentationAvailability ProbeAvailability() => D3D11DeviceResources.Probe(_options.DeviceSelection);

    public IMediaPresentationCoordinator Create(
        IMediaEngine mediaEngine,
        MediaPlayerConfiguration configuration)
    {
        var backend = new NativeD3D11PresentationBridge(configuration, _options);
        var rasterizer = new SubtitleTextRenderer(configuration.SubtitleStyle, configuration.KaraokeStyle);
        return new MediaPresentationCoordinator(
            backend,
            rasterizer,
            mediaEngine,
            configuration,
            _precisionWaiterFactory);
    }
}
