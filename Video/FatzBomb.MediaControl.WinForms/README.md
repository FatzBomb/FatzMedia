# FatzBomb.MediaControl.WinForms

WinForms adapter for `FatzBomb.MediaControl` (Stage 027A slice 3, Stage 046A): `MediaSurface`,
an HWND control attached to any `IMediaControlSession` as a presentation surface, and
`SecondaryPresentationWindow` for a second screen.

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

Or use the helper window (Stage 046A, the same shape as the Avalonia and WPF adapters):

```csharp
var singer = new SecondaryPresentationWindow(player);            // surface id "secondary"
singer.ShowOnScreen(Screen.AllScreens[1], fullScreen: true, owner: this);
// F11 or a double-click toggles full screen, Escape leaves it; closing it detaches.
```

The renderer presents into a child window of the surface. The surface hides that child and
paints `BackColor` instead of a stale frame (the same rules as the WPF and Avalonia surfaces,
from Core's `MediaSurfaceContentTracker`):

- while the player is in Audio mode (`IsPresentationSuspended`);
- with `AutoHide` (default true) until the first frame of the current media, and after stop,
  close, an error or a natural end without looping (`IsContentVisible`,
  `ContentVisibilityChanged`).

The child passes mouse input to the surface, so its mouse events, double-click and drops work
as on any control. `PresentationPath` is `NativeWindow` while attached (with
`PresentationPathReason`, `PresentationPathChanged` and an Info diagnostic owned by the player);
a surface the player refuses raises one Warning. The surface sends the live-resize hint while
its form is being resized. Its own events are raised on the UI thread.
