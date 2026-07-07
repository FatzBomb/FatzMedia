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

## Device-Lost Recovery

Core detects and recovers D3D11 device loss (Stages 005A/005B/005C) entirely inside Core; adapters are never involved.

- `D3D11DeviceLostClassifier` classifies device-removed/reset conditions on both sides: HRESULT-based for the Core-owned presentation device, and an `IsDeviceLost(IntPtr)` overload for the FFmpeg-owned hardware-decode device (a raw native pointer, wrapped best-effort and never false-positive).
- Two independent recovery orchestrators, matching the two independently owned devices: the presentation side in `NativeMediaPresentationCoordinator` (rebuilds device, swap chains, DirectComposition target, and secondary outputs), and the decode side in `VideoPipeline` (runtime software-decode fallback with background hardware re-probe/restore; audio and CDG keep running).
- Diagnostic categories follow a fixed naming convention: `"D3D11.DeviceLost"` (presentation) and `"Decoder.DeviceLost"` (decode), raised at Error on loss and Info on successful recovery, with a `.RecoveryFailed` suffix for the terminal diagnostic after bounded retries are exhausted.
- Both recovery paths reuse existing rebuild machinery (the proven surface-recreate and seek stop-reposition-restart sequences) — no new swap-chain, compositor, or renderer abstraction was introduced.

## WPF Adapter API

`FatzBombMedia.MediaControl.Wpf.Player` derives from Core `MediaControlPlayer`.

This makes Core commands, properties, and events automatically available through the WPF adapter while keeping adapter-specific additions limited to WPF conversions such as `ImageSource` to `MediaInputSource`.

`MediaSurface` accepts an `IMediaControlSession`/player and supplies HWND surfaces to Core. Its `OverlayContent` is WPF UI chrome only; it is not a media overlay. Media overlay, wash, background, and final composition stay in Core.
