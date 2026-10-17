# FatzBomb.MediaControl.Avalonia

Avalonia adapter for `FatzBomb.MediaControl` (Stage 027A slice 1).

```xml
<fbm:MediaSurface Player="{Binding Player}" VideoDisplayMode="Zoom" />
```

- `MediaSurface` attaches one primary managed frame-sink surface to any
  `IMediaControlSession`, resizes it with layout bounds and render scaling, detaches when
  removed from the visual tree or when `Player` changes, and draws the final frames.
- `AvaloniaMediaInputSources.FromBitmap` converts Avalonia bitmaps to Core sources.
- Rendering stays in the renderer the host selected (software or Vulkan frame sinks today);
  platform services come from the host's platform factory (Windows or Linux).
- Session events may arrive on background threads; marshal with
  `Dispatcher.UIThread.Post` before touching UI state.
