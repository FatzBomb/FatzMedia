# FatzBomb.MediaControl

FatzBomb.MediaControl is a media-control solution with a UI-agnostic Core engine, a strict WPF adapter, and a small WPF validation player.

## Projects

| Project | Purpose |
|---|---|
| `FatzBomb.MediaControl` | Portable Core playback, commands, media state, sidecar detection, audio/video/Cdg, retained composition models, and platform/renderer contracts. |
| `FatzBomb.MediaControl.Rendering.Direct3D11` | Direct3D 11 renderer, composition, swap-chain, and presentation implementation. |
| `FatzBomb.MediaControl.Windows` | Windows platform composition, native-library loading, endpoint integration, and renderer selection. |
| `FatzBomb.MediaControl.Wpf` | WPF adapter for control/HWND lifecycle interop and WPF-specific input conversions. It selects Windows composition but must not own renderer, pipeline, layer, timing, or final presentation logic. |
| `FatzBomb.MediaControl.Player` | WPF validation host used to exercise the Core engine through the WPF adapter. |

Future UI integrations should be separate adapter projects using the same package style, for example `FatzBomb.MediaControl.WinUI`, `FatzBomb.MediaControl.Avalonia`, and `FatzBomb.MediaControl.WinForms`.

## Current Rule

`FatzBomb.MediaControl` owns media-session orchestration and the neutral output model. Platform composition selects a renderer, while adapters supply framework/native surfaces and route framework-specific controls or source types into Core APIs.

The WPF adapter exposes `FatzBomb.MediaControl.Wpf.Player`, which derives from Core `MediaControlPlayer`. Core commands, properties, and events are inherited automatically; WPF-specific additions should stay limited to conversions such as `ImageSource`/`BitmapSource` to `MediaInputSource`.

## Native DLLs

BASS and FFmpeg native DLLs are not included. The host app/test shell supplies them through configured native paths. For local Player testing, put x64 native DLLs in `FatzBomb.MediaControl.Player/native/`.

## Active Docs

- `docs/ProjectAnalysisRoadmap [2026-06-30].md`
- `docs/CURRENT_ARCHITECTURE.md`
- `docs/VALIDATION.md`
