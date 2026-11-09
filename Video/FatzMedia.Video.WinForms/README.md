# FatzMedia.Video.WinForms

WinForms adapter for `FatzMedia.Video` (Stage 027A slice 3, Stage 046A): `MediaSurface`,
an HWND control attached to any `IPlayerSession` as a presentation surface (with a frame-sink
fallback and overlay content), `SecondaryPresentationWindow` for a second screen, the
`KaraokeSurface` and `BackgroundSurface` controls, and `WinFormsMediaInputSources`.

```csharp
var player = new MediaControlPlayer(new WindowsDirect3D11PlatformFactory(), MediaPlayerConfiguration.Auto, ["native"]);
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
// F11 or a double-click toggles full screen; Escape leaves it, or closes a windowed one;
// closing it detaches.
```

The renderer presents into a child window of the surface. The child stays hidden until the
renderer presented into it (after each attach and after Audio mode), so the surface shows
`BackColor`, never leftover pixels, before the first frame. The surface also hides that child and
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

## Frame-sink path (Stage 046A slice 2)

When the session's renderer does not present into windows (the software renderer, or any renderer
that presents only through frame sinks), the HWND attach is refused and the surface attaches a
managed frame sink instead: the renderer's final premultiplied BGRA frames are copied to a GDI+
bitmap on the UI thread (two alternating buffers, latest frame wins, no per-frame allocation)
and drawn double-buffered in `OnPaint`, so a paint never shows a half-written frame.
`PresentationPath` is `FrameSink` with the reason; `PreferNativeWindow = false` forces this path
with any renderer. Auto-hide and the Audio-mode clear work as on the window path (hidden content
is dropped). Expect a CPU copy per frame: for full-HD video use a renderer that presents into the
window.

## First frame ready (Stage 051A)

`FirstFrameReady` is raised on the UI thread once per load of media with visual content, when a frame
of that load is on the surface (frame sink: published and painted; window path: the child window is
shown after the swap chain presented it). Subscribe before `LoadAsync`. For an A/B swap keep the
waiting surface at its size with `Visible = false`, then in the handler show it and hide the old one.
Same-frame changes with other controls are best effort on WinForms (the WPF adapter's composited mode
is the exact one; see its README).

## Overlay content

`OverlayContent` (a `Control`) is shown above the video in a borderless layered window owned by
the surface's form, like the WPF adapter's overlay window, on either path. It follows the
surface's screen position, size and visibility (moves of the form or any parent included) and
minimizes with the form. The window's background is a colour key (a near-black, RGB 1,0,1), so a
control with `BackColor = Color.Transparent` shows the video around its children; anti-aliased
edges blend towards that dark key. It never takes the keyboard focus. `OverlayHitTestVisible`
(default false) makes it take mouse input; otherwise clicks pass through to the surface. The
surface never disposes the overlay control.

## KaraokeSurface and BackgroundSurface

The same self-contained controls as the WPF and Avalonia adapters, over Core's
`LightweightPlayerPolicy` and `LightweightPlayerController`. Each creates its own player from
`PlatformFactory` (and `NativeLibraryPaths`) on the first load and releases it when its handle is
destroyed; a new handle loads `Source` again.

```csharp
var background = new BackgroundSurface(new WindowsDirect3D11PlatformFactory()) { Dock = DockStyle.Fill };
background.OverlayContent = new Label { Text = "Up next", BackColor = Color.Transparent, AutoSize = true };
background.Source = @"C:\loops\stage.mp4";   // or a Color, a Bitmap, a MediaInputSource
var karaoke = new KaraokeSurface(platform) { Dock = DockStyle.Fill, Source = @"C:\songs\song.zip" };
```

- `KaraokeSurface`: CD+G and MIDI/KAR on a 4:3 surface centred on `CanvasColor`; video and plain
  audio are rejected after inspection (closed).
- `BackgroundSurface`: a `Color` (no player), an image or video path, a `Bitmap` or a
  `MediaInputSource`, played in `MediaPlayerMode.Background` (looping, no audio engine);
  `BackgroundColor`, `SetColor`, `OverlayContent`, `OverlayHitTestVisible`.
- Both: `Source`, `AutoPlay`, `AutoHide`, `IsContentVisible`, `ContentVisibilityChanged`,
  `Session`, `LoadAsync`, `PlayAsync` / `PauseAsync` / `StopAsync`. Instead of the WPF
  `PresentationMode` they have `PreferNativeWindow` (the inner surface's path choice).

`WinFormsMediaInputSources.FromBitmap(Bitmap, displayName)` converts a GDI+ bitmap to a Core BGRA
source (straight alpha, like the WPF and Avalonia conversions).
