# FatzBomb.MediaControl

FatzBomb.MediaControl is a media-control solution with a UI-agnostic Core engine, a strict WPF adapter, and a small WPF validation player.

## Projects

| Project | Purpose |
|---|---|
| `FatzBomb.MediaControl` | Core playback, commands, media state, sidecar detection, Cdg, retained layers, D3D11 composition, swap chains, presentation targets, and final rendering. |
| `FatzBomb.MediaControl.Windows` | Windows platform composition, native-library loading, and concrete media-engine registrations. |
| `FatzBomb.MediaControl.Wpf` | WPF adapter for control/HWND lifecycle interop and WPF-specific input conversions. It selects Windows composition but must not own renderer, pipeline, layer, timing, or final presentation logic. |
| `FatzBomb.MediaControl.Player` | WPF validation host used to exercise the Core engine through the WPF adapter. |

Future UI integrations should be separate adapter projects using the same package style, for example `FatzBomb.MediaControl.WinUI`, `FatzBomb.MediaControl.Avalonia`, and `FatzBomb.MediaControl.WinForms`.

## Current Rule

`FatzBomb.MediaControl` owns the media output. Adapters present a framework/native surface to Core and route framework-specific controls or source types into Core APIs.

The WPF adapter exposes `FatzBomb.MediaControl.Wpf.Player`, which derives from Core `MediaControlPlayer`. Core commands, properties, and events are inherited automatically; WPF-specific additions should stay limited to conversions such as `ImageSource`/`BitmapSource` to `MediaInputSource`.

## Native DLLs

BASS and FFmpeg native DLLs are not included. The host app/test shell supplies them through configured native paths. For local Player testing, put x64 native DLLs in `FatzBomb.MediaControl.Player/native/`.

## Active Docs

- `docs/ProjectAnalysisRoadmap [2026-06-30].md`
- `docs/CURRENT_ARCHITECTURE.md`
- `docs/VALIDATION.md`
