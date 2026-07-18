# FatzBomb.MediaControl.Windows.Direct3D11

Convenience integration package combining `FatzBomb.MediaControl.Windows` platform
services with the `FatzBomb.MediaControl.Rendering.Direct3D11` backend.

Pass `WindowsDirect3D11MediaControlPlatformFactory` to a WPF `Player` or directly to
`MediaControlPlayer`. Advanced hosts can reference the Windows and renderer packages
separately and inject another renderer factory.
