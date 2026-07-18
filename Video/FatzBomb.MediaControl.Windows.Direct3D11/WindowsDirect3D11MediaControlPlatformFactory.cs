using FatzBomb.MediaControl.Rendering.Direct3D11;
using FatzBomb.MediaControl.Windows.Threading;

#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>
/// Convenience composition root combining Windows platform services with the Direct3D11 renderer.
/// </summary>
public sealed class WindowsDirect3D11MediaControlPlatformFactory : WindowsMediaControlPlatformFactory
{
    public WindowsDirect3D11MediaControlPlatformFactory()
        : base(new Direct3D11PresentationCoordinatorFactory(new WindowsPrecisionWaiterFactory()))
    {
    }
}
