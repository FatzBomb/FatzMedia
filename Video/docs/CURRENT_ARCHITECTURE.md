# Current Architecture

## Projects

- `FatzBombMedia.MediaControl`: UI-agnostic Core media-control engine.
- `FatzBombMedia.MediaControl.Wpf`: strict WPF adapter for control/HWND lifecycle interop.
- `FatzBombMedia.Player`: WPF validation host.

Future adapters should be separate projects using the same package pattern:

- `FatzBombMedia.MediaControl.WinUI`
- `FatzBombMedia.MediaControl.Avalonia`
- `FatzBombMedia.MediaControl.WinForms`

## Ownership

Core owns playback, commands, media state, sidecar detection, background image/video lifecycle, Cdg, wash, retained layers, D3D11 composition, swap-chain creation, target registration, multi-target fan-out, and final presentation.

Adapters own only framework interop: control lifecycle, native surface creation, DPI/size, visibility, focus/input bridge, and framework-specific source conversion helpers.

Adapters must not own renderer selection, layer roles, frame processing, Cdg invalidation, background/video presentation, compositor timing, swap-chain policy, or final present logic.

## Runtime Flow

1. Host places a framework control, such as WPF `MediaSurface`.
2. Adapter creates or discovers a framework/native presentation surface.
3. Adapter passes a neutral `MediaPresentationSurfaceDescriptor` to Core.
4. Core attaches/resizes/detaches that surface.
5. Core builds the final composed frame from Layer 0 background/base, Layer 1 wash, and Layer 2 Cdg overlay.
6. Core presents the composed frame to all active targets.

## WPF Adapter API

`FatzBombMedia.MediaControl.Wpf.Player` derives from Core `MediaControlPlayer`.

This makes Core commands, properties, and events automatically available through the WPF adapter while keeping adapter-specific additions limited to WPF conversions such as `ImageSource` to `MediaInputSource`.

`MediaSurface` accepts an `IMediaControlSession`/player and supplies HWND surfaces to Core. Its `OverlayContent` is WPF UI chrome only; it is not a media overlay. Media overlay, wash, background, and final composition stay in Core.
