using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Threading;

namespace FatzBomb.MediaControl.Rendering.Direct3D11;

/// <summary>
/// Creates the Direct3D 11 presentation coordinator.
/// </summary>
public sealed class Direct3D11PresentationCoordinatorFactory : IMediaPresentationCoordinatorFactory
{
    private readonly IPrecisionWaiterFactory _precisionWaiterFactory;

    public Direct3D11PresentationCoordinatorFactory(IPrecisionWaiterFactory precisionWaiterFactory)
    {
        _precisionWaiterFactory = precisionWaiterFactory ?? throw new ArgumentNullException(nameof(precisionWaiterFactory));
    }

    public string BackendName => "Direct3D11";

    public IMediaPresentationCoordinator Create(
        IMediaEngine mediaEngine,
        MediaPlayerConfiguration configuration) =>
        new Direct3D11MediaPresentationCoordinator(mediaEngine, configuration, _precisionWaiterFactory);
}
