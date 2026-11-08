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
        : this(new Direct3D11PresentationOptions())
    {
    }

    public WindowsDirect3D11MediaControlPlatformFactory(Direct3D11PresentationOptions options)
        : base(CreatePresentationFactory(options))
    {
    }

    /// <summary>
    /// Creates the Direct3D11 presentation factory this composition uses, for hosts that combine it
    /// with other renderers (Stage 036A: a <c>FallbackPresentationCoordinatorFactory</c>).
    /// </summary>
    public static Direct3D11PresentationCoordinatorFactory CreatePresentationFactory(Direct3D11PresentationOptions options) =>
        new(new WindowsPrecisionWaiterFactory(), options);
}
