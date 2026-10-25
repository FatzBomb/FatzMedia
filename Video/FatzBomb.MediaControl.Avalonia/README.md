# FatzBomb.MediaControl.Avalonia

Avalonia adapter for `FatzBomb.MediaControl` (Stage 027A slices 1, 2 and 5).

```xml
<fbm:MediaSurface Player="{Binding Player}" VideoDisplayMode="Zoom" />
```

- `MediaSurface` attaches one surface to any `IMediaControlSession`, resizes it with
  layout bounds and render scaling, detaches when removed from the visual tree or when
  `Player`, `Role` or `SurfaceId` changes, and shows the final frames.
- **Second screen** (slice 5): put another `MediaSurface` with
  `Role="Projector"` (and a distinct `SurfaceId` per extra screen) in any window bound to the
  same player, or use the helper window:

  ```csharp
  var singer = new SecondaryPresentationWindow(player);          // surface id "secondary"
  singer.ShowOnScreen(Screens.All[1], fullScreen: true, owner: this);
  // F11 or a double-click toggles full screen, Escape leaves it; closing it detaches.
  ```

  Every renderer (software, Vulkan, Direct3D11) renders each frame once per surface at that
  surface's size, and each surface picks its own path below.
- While the player is in Audio mode (no renderer presents), the surface shows only its
  `Background` instead of the last frame (`IsPresentationSuspended`).
- Two paths (`PresentationPath`, with the reason in `PresentationPathReason`, plus an Info
  diagnostic):
  - **Shared GPU image** (`PreferGpuSharedSurface`, default true): when Avalonia's
    compositor offers `ICompositionGpuInterop` and the renderer can export a matching
    handle kind (Direct3D11 keyed-mutex textures on Windows, Vulkan opaque-fd images on
    Linux), frames go straight into Avalonia's compositor with no CPU copy.
  - **Frame sink**: the renderer reads frames back and the surface uploads them as a
    bitmap. Used when the GPU path is off, unsupported, or has failed.
- `AvaloniaMediaInputSources.FromBitmap` converts Avalonia bitmaps to Core sources.
- Rendering stays in the renderer the host selected (software, Vulkan, or Direct3D11);
  platform services come from the host's platform factory (Windows or Linux).
- Session events may arrive on background threads; marshal with
  `Dispatcher.UIThread.Post` before touching UI state.
