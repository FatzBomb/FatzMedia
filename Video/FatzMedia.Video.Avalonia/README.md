# FatzMedia.Video.Avalonia

Avalonia adapter for `FatzMedia.Video` (Stage 027A slices 1, 2 and 5).

```xml
<fbm:MediaSurface Player="{Binding Player}" VideoDisplayMode="Zoom" />
```

- `MediaSurface` attaches one surface to any `IPlayerSession`, resizes it with
  layout bounds and render scaling, detaches when removed from the visual tree or when
  `Player`, `Role` or `SurfaceId` changes, and shows the final frames.
- **Second screen** (slice 5): put another `MediaSurface` with
  `Role="Projector"` (and a distinct `SurfaceId` per extra screen) in any window bound to the
  same player, or use the helper window:

  ```csharp
  var singer = new SecondaryPresentationWindow(player);          // surface id "secondary"
  singer.ShowOnScreen(Screens.All[1], fullScreen: true, owner: this);
  // F11 or a double-click toggles full screen; Escape leaves it, or closes a windowed one;
// closing it detaches.
  ```

  Every renderer (software, Vulkan, Direct3D11) renders each frame once per surface at that
  surface's size, and each surface picks its own path below.
- While the player is in Audio mode (no renderer presents), the surface shows only its
  `Background` instead of the last frame (`IsPresentationSuspended`).
- **Auto-hide** (Stage 046A, as the WPF and WinForms surfaces, from Core's
  `MediaSurfaceContentTracker`): with `AutoHide` (default true) the surface shows its
  `Background` until the first frame of the current media, and after stop, close, an error or a
  natural end without looping (`IsContentVisible`, `ContentVisibilityChanged`). Only a primary
  surface forwards `VideoDisplayMode`, so a second screen never changes it. A surface the player
  refuses raises one Warning.
- `PresentationPath` is Core's `FatzMedia.Video.MediaSurfacePresentationPath` since Stage
  046A (it was `FatzMedia.Video.Avalonia.MediaSurfacePresentationPath`). Code with
  `using FatzMedia.Video;` compiles unchanged; a fully qualified name drops `.Avalonia`;
  compiled consumers need a rebuild.
- Two paths (`PresentationPath`, with the reason in `PresentationPathReason`, plus an Info
  diagnostic owned by the attached player, so with several players each log shows only its own
  surfaces):
  - **Shared GPU image** (the default): when Avalonia's compositor offers GPU interop
    (`ICompositionGpuInterop` and a render-thread import) and the renderer can export a
    matching handle kind (Direct3D11 keyed-mutex textures on Windows, Vulkan opaque-fd images on
    Linux), frames go straight into Avalonia's compositor with no CPU copy. A composition custom
    visual imports and draws them on Avalonia's **render thread**, so the picture keeps moving
    while the UI thread is busy (drop-downs, window loads; ENG-066). This uses Avalonia's
    `[Unstable]` render-interface API, kept in `RenderThreadGpuImport.cs` and pinned with the exact
    Avalonia version in the csproj (re-check that file and run the manual smoke, `VALIDATION.md` 57, whenever Avalonia is upgraded); if it throws or is missing, the surface falls back by itself.
  - **Frame sink** (the automatic fallback: no GPU interop, a renderer that cannot export, a
    lost graphics context, or any failure of the path above): the renderer's thread copies each
    frame straight into one of three rotating bitmaps and the same kind of render-thread visual
    draws it, so it keeps moving while the UI thread is busy too (about one CPU core more at
    3440x1416 and 60 fps). The environment variable `FATZMEDIA_AVALONIA_FRAMESINK=1` forces it,
    for comparisons and bug reports (diagnostics only, not an app option).
- `AvaloniaMediaInputSources.FromBitmap` converts Avalonia bitmaps to Core sources (the same
  shape as the WPF `WpfMediaInputSources.FromImageSource` and `WinFormsMediaInputSources.FromBitmap`).
- **`KaraokeSurface` and `BackgroundSurface`** (Stage 046A slice 2, as in the WPF and WinForms
  adapters): self-contained controls that create their own player from `PlatformFactory` (and
  `NativeLibraryPaths`), with Core's rules (`LightweightPlayerPolicy`, `LightweightPlayerController`).
  `KaraokeSurface` shows CD+G and MIDI/KAR on a 4:3 surface centred on `CanvasColor` and rejects
  video and plain audio after inspection. `BackgroundSurface` shows a `Color`/`ISolidColorBrush`
  (no player), a path, an Avalonia `Bitmap` or a `MediaInputSource` in `MediaPlayerMode.Background`
  (looping, no audio engine), with `OverlayContent` composited above it (`OverlayHitTestVisible`,
  default false). Both have `Source`, `AutoPlay`, `AutoHide`, `IsContentVisible`,
  `ContentVisibilityChanged`, `Session`, `LoadAsync`, `PlayAsync`/`PauseAsync`/`StopAsync` and
  `Dispose`. Differences from WPF: no `PresentationMode` (the surface chooses its own path);
  the player is released when the control leaves the visual tree.

  ```xml
  <fbm:BackgroundSurface PlatformFactory="{Binding Platform}" Source="{Binding BackgroundPath}">
      <fbm:BackgroundSurface.OverlayContent>
          <TextBlock Text="Up next" />
      </fbm:BackgroundSurface.OverlayContent>
  </fbm:BackgroundSurface>
  ```
- Rendering stays in the renderer the host selected (software, Vulkan, or Direct3D11);
  platform services come from the host's platform factory (Windows or Linux).
- Session events may arrive on background threads; marshal with
  `Dispatcher.UIThread.Post` before touching UI state.

## Differences between the three adapters

The three `MediaSurface` controls share the same surface members (`Player`, `Role`, `SurfaceId`, `AutoHide`,
`IsContentVisible`, `FirstFrameReady`, `PresentationPath` and the rest). What differs is how the picture reaches the
screen and a few extras:

| Item | WPF | Avalonia | WinForms |
| --- | --- | --- | --- |
| Choosing the path | `PresentationMode`: `NativeHwndOverlay` (default) or `WpfComposited` | no option: the shared GPU image on the render thread, else the frame sink on its own | `PreferNativeWindow` (default true), else the frame sink |
| `OverlayContent`, `OverlayHitTestVisible` | yes, a `UIElement` | yes, any control, composited above the picture (added 2026-11-12) | yes, a `Control` in a borderless layered window |
| Composited-mode figures (`CompositedFramesPublished`, `CompositedUiThreadTime`) and `RefreshPresentationLayout` | yes | no | no |
| `AttachedPixelSize` | no | yes | no |
| Screen helpers `PresentationScreen` and `WindowFullScreen` | yes (in `FatzMedia.Video.Wpf`) | no: use `SecondaryPresentationWindow.ShowOnScreen(screen, fullScreen)` | no: use `SecondaryPresentationWindow.ShowOnScreen(screen, fullScreen)` |
| Second screen | open a `SecondaryPresentationWindow` | the same | the same |
| Hiding a waiting surface for an A/B swap | `Visibility.Hidden` | `Opacity = 0` (not `IsVisible = false`) | `Visible = false` |

The old WPF `MediaSurface.ShowSecondaryOutput` family (`ShowSecondaryOutput`, `CloseSecondaryOutput`,
`IsSecondaryOutputOpen`, `SecondaryOutputsChanged`) was removed on 2026-11-12; every host opens a
`SecondaryPresentationWindow` itself.

## First frame ready (Stage 051A)

`MediaSurface.FirstFrameReady` is raised on the UI thread once per load of media with visual content,
when a frame of that load is on the surface: on the frame-sink path the bitmap holds it and the
control is invalidated (changes made in the handler render in the same frame); on the shared GPU
image path the image goes to the compositor on its own (best effort). Subscribe before `LoadAsync`.
For an A/B swap hide the waiting surface with `Opacity = 0`, not `IsVisible = false` (an invisible
control is not laid out), and in the handler show it and hide the old one.
