using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Threading;
using FatzBomb.MediaControl.Rendering.Direct3D11.Subtitle;
using NativeD3D11PresentationBridge = FatzBomb.MediaControl.Rendering.Direct3D11.NativeD3D11PresentationBridge;

namespace FatzBomb.MediaControl.Rendering.Direct3D11;

public sealed class Direct3D11PresentationCoordinatorFactory : IMediaPresentationCoordinatorFactory
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
