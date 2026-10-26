# FatzBomb.MediaControl.WinForms

WinForms adapter for `FatzBomb.MediaControl` (Stage 027A slice 3): `MediaSurface`, an
HWND control attached to any `IMediaControlSession` as the primary presentation surface.

```csharp
var player = new MediaControlPlayer(new WindowsDirect3D11MediaControlPlatformFactory(), MediaPlayerConfiguration.Auto, ["native"]);
var surface = new MediaSurface { Dock = DockStyle.Fill, Player = player };
```

Use a DPI-aware WinForms application (`ApplicationHighDpiMode.PerMonitorV2`) so
`ClientSize` is in device pixels. Session events arrive on background threads; marshal
with `Control.BeginInvoke`.

A second screen of the same session is another `MediaSurface` with `Role =
MediaPresentationSurfaceRole.Projector` (Stage 044A): it attaches as `SurfaceId`, or
`DefaultSecondarySurfaceId` ("secondary") when unset, and does not forward its
`VideoDisplayMode`. Changing `Role` or `SurfaceId` re-attaches the surface.

```csharp
var second = new MediaSurface { Dock = DockStyle.Fill, Role = MediaPresentationSurfaceRole.Projector, Player = player };
```
