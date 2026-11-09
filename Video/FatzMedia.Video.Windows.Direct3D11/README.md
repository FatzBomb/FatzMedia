# FatzMedia.Video.Windows.Direct3D11

Convenience integration package combining `FatzMedia.Video.Windows` platform
services with the `FatzMedia.Video.Direct3D11` backend.

Pass `WindowsDirect3D11PlatformFactory` to a WPF `Player` or directly to
`PlayerCore`. Advanced hosts can reference the Windows and renderer packages
separately and inject another renderer factory.

Direct3D11-specific options are configured at the integration boundary:

```csharp
var platformFactory = new WindowsDirect3D11PlatformFactory(
    new Direct3D11PresentationOptions
    {
        NativeSwapChainPresentMode = NativeSwapChainPresentMode.Immediate
    });
```

Immediate mode is primarily diagnostic. The parameterless factory uses VSync.
