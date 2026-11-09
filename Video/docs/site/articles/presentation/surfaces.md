# Surfaces

A surface is the UI control that shows what the renderer draws. Each adapter has one, named `MediaSurface`: <xref:FatzMedia.Video.Wpf.MediaSurface>, <xref:FatzMedia.Video.Avalonia.MediaSurface> and <xref:FatzMedia.Video.WinForms.MediaSurface>. You put it in your window, set its `Player`, and it attaches itself to the player, resizes with the layout and detaches when it goes away. The surface decides how frames get from the renderer onto the screen; this article explains the choices, how the adapter makes them and how to see what it chose.

> [!NOTE]
> A surface owns only its control and its input plumbing. Playback, timing, composition and the choice of renderer belong to the player. See [Renderers](../video/renderers.md).

## Presentation paths

<xref:FatzMedia.Video.MediaSurfacePresentationPath> names how frames reach the screen:

| Path | What happens | Used by |
| --- | --- | --- |
| `NativeWindow` | The renderer presents straight into a native window (an HWND swap chain) that the surface owns. No CPU copy, best performance. The native window sits above the UI framework's own content, so ordinary UI cannot be layered over it (use `OverlayContent`). | WPF (default), WinForms |
| `SharedGpuImage` | The renderer exports double-buffered GPU images that the UI framework's compositor imports and shows directly. No CPU copy, and normal UI layering works. | Avalonia |
| `FrameSink` | The renderer reads each finished frame back to the CPU and the surface shows it as a bitmap. Works with every renderer. Costs a copy per frame. | All adapters, as the fallback |
| `None` | No surface is attached. | |

The Direct3D11 renderer can also use a DirectComposition target for layered overlays. It is an internal mechanism that shows up in some diagnostics; you do not configure it.

## How each adapter chooses

### WPF

<xref:FatzMedia.Video.Wpf.WpfPresentationMode> decides. The default, `NativeHwndOverlay`, uses the `NativeWindow` path. `WpfComposited` uses the `FrameSink` path: the final Direct3D11 composition is read back into the WPF visual tree, so normal WPF clipping, transforms, opacity and z-order apply, at the cost of a synchronous GPU readback and a lower practical frame rate.

```xaml
<fbm:MediaSurface x:Name="Surface"
                  Player="{Binding Player}"
                  VideoDisplayMode="Zoom"
                  PresentationMode="NativeHwndOverlay" />
```

Set `PresentationMode` before you load media. While media is open a change is deferred until it closes (an Info diagnostic says so). Mixing modes inside one player session is not supported.

A renderer that cannot draw into a window (Software, Vulkan) needs `WpfComposited`; otherwise the player refuses the surface (see [Attach warnings](#attach-warnings)).

`OverlayContent` puts WPF content above the video. The surface shows it in a transparent window owned by your window, which follows the surface. Set `OverlayHitTestVisible` (default `false`) when the overlay has controls that must take input.

### WinForms

The surface hosts a child window and tries `NativeWindow` first. It falls back to `FrameSink` when the renderer does not present into windows (Software, Vulkan), or when you set `PreferNativeWindow = false`. Changing `PreferNativeWindow` re-attaches the surface.

```csharp
var surface = new MediaSurface
{
    Dock = DockStyle.Fill,
    Player = player,
    PreferNativeWindow = true,
    AutoHide = true,
};
```

Run the application DPI-aware (`ApplicationHighDpiMode.PerMonitorV2`) so the surface's size is in device pixels. `OverlayContent` (a `Control`) works as in WPF, in a layered window above the video.

### Avalonia

The surface never uses a native window. With `PreferGpuSharedSurface` (default `true`) it uses `SharedGpuImage` when Avalonia's compositor offers GPU interop and the renderer can export a handle kind it imports (Direct3D11 keyed-mutex textures on Windows, Vulkan opaque-fd images on Linux). Otherwise it uses `FrameSink`. The reason for a fallback is available in `PresentationPathReason`, for example `Avalonia's rendering backend offers no GPU interop (ICompositionGpuInterop)` or `PreferGpuSharedSurface is off`.

```xaml
<fbm:MediaSurface Player="{Binding Player}"
                  VideoDisplayMode="Zoom"
                  PreferGpuSharedSurface="True" />
```

## Seeing what was chosen

Every surface reports its path:

```csharp
Surface.PresentationPathChanged += (_, _) =>
    Title = $"{Surface.PresentationPath}: {Surface.PresentationPathReason}";
```

| Member | Meaning |
| --- | --- |
| `PresentationPath` | The current <xref:FatzMedia.Video.MediaSurfacePresentationPath>; `None` while detached. |
| `PresentationPathReason` | Why that path was chosen, or `null`. |
| `PresentationPathChanged` | Raised on the UI thread when the path or the reason changes. |

Each choice is also logged to the attached player's `MediaDiagnostic` event as an Info diagnostic in the surface's category (`Wpf.MediaSurface`, `Avalonia.MediaSurface`, `WinForms.MediaSurface`):

```text
[Info] Wpf.MediaSurface: Surface #1 'primary': native window path (PresentationMode is NativeHwndOverlay)
[Info] Avalonia.MediaSurface: Surface #1 'primary': frame sink path (Avalonia's rendering backend offers no GPU interop (ICompositionGpuInterop))
```

The diagnostics belong to the player the surface is attached to, so with several players each log lists only its own surfaces. See [Multiple players](multiple-players.md).

### Attach warnings

If the player does not accept the surface, the surface raises one warning (again only after a successful attach or a new player):

```text
[Warning] Wpf.MediaSurface: Surface #1 'primary': the player did not accept the surface, so nothing is shown on it (no renderer, or the renderer rejected the surface; its own diagnostics say why). The surface tries again on its next resize.
```

The usual cause is a renderer/surface mismatch, such as the Software renderer with a WPF `NativeHwndOverlay` surface. Look at the renderer's own diagnostics, and see [Common problems](../troubleshooting/common-problems.md).

## Showing and hiding: AutoHide

With `AutoHide` on (the default) the surface hides its video until there is something to show, and shows its background (black in WPF, `Background` in Avalonia, `BackColor` in WinForms) instead of a stale frame. The decision comes from <xref:FatzMedia.Video.MediaSurfaceContentTracker>, so all three adapters behave alike:

- A load starts waiting. The content becomes visible when the first frame of the media is presented.
- Stop, close, an error, or a natural end without looping hide the content again.
- Starting or looping again shows it once a new frame arrives.
- Pause keeps the last frame.

| Member | Meaning |
| --- | --- |
| `AutoHide` | Default `true`. Set `false` to keep the last frame visible after stop. |
| `IsContentVisible` | Whether auto-hide shows the content now. Always `false` while `AutoHide` is off (the content is then simply shown). |
| `ContentVisibilityChanged` | Raised on the UI thread when `IsContentVisible` changes. |
| `IsPresentationSuspended` | `true` while the player is in `Audio` mode: nothing presents, so the surface shows its background rather than the last frame. |

`AutoHide` never changes the control's own `Visibility`. Use `ContentVisibilityChanged` to show or hide your own chrome, for example a cover image while audio plays.

## Surface identity and roles

A player can have several surfaces. Each has a `Role` (`Primary`, the default, or `Projector`) and a `SurfaceId`. `EffectiveSurfaceId` is the id it attaches with: `primary` for a primary surface, `secondary` for any other role when `SurfaceId` is blank. Only a primary surface forwards its `VideoDisplayMode` to the player. See [Second screen](second-screen.md).
