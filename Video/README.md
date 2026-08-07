# FatzBomb.MediaControl

FatzBomb.MediaControl is a media-control solution with a UI-agnostic Core engine, a strict WPF adapter, and a small WPF validation player.

## Projects

| Project | Purpose |
|---|---|
| `FatzBomb.MediaControl` | Portable Core playback, commands, media state, sidecar detection, audio/video/Cdg, retained composition models, and platform/renderer contracts. |
| `FatzBomb.MediaControl.Rendering.Direct3D11` | Direct3D 11 renderer backend, GPU composition, swap-chain, and presentation implementation. |
| `FatzBomb.MediaControl.Rendering.Software` | Portable deterministic software renderer — in-memory premultiplied BGRA8 composition with no GPU or window. |
| `FatzBomb.MediaControl.Windows` | Windows native-library loading, endpoint integration, timing, and hardware-decode policy. |
| `FatzBomb.MediaControl.Windows.Direct3D11` | Convenience integration bundle pairing Windows services with the current renderer. |
| `FatzBomb.MediaControl.Wpf` | WPF adapter for control/HWND lifecycle interop and WPF-specific input conversions. It receives platform composition from the host and owns no renderer, pipeline, layer, timing, or final presentation logic. |
| `FatzBomb.MediaControl.Player` | WPF validation host used to exercise the Core engine through the WPF adapter. |

Future UI integrations should be separate adapter projects using the same package style, for example `FatzBomb.MediaControl.WinUI`, `FatzBomb.MediaControl.Avalonia`, and `FatzBomb.MediaControl.WinForms`.

## Current Rule

`FatzBomb.MediaControl` owns media-session orchestration and the neutral output model.
The host composition root selects platform services and a renderer, while adapters
supply framework/native surfaces and route framework-specific controls or source
types into Core APIs.

The WPF adapter exposes `FatzBomb.MediaControl.Wpf.Player`, which derives from Core `MediaControlPlayer`. Core commands, properties, and events are inherited automatically; WPF-specific additions should stay limited to conversions such as `ImageSource`/`BitmapSource` to `MediaInputSource`.

## Native DLLs

BASS and FFmpeg native DLLs are not included. The host developer supplies and
licenses them through predefined or custom native paths. For local Player testing,
put x64 native DLLs in `FatzBomb.MediaControl.Player/native/`. See
`docs/NATIVE_DEPENDENCIES.md` for filenames, supported versions/RIDs, approved
sources, licensing cautions, package layout, and the update process.

## Active Docs

- `docs/roadmap/STAGE_MANIFEST.md`
- `docs/CURRENT_ARCHITECTURE.md`
- `docs/VALIDATION.md`
- `docs/NATIVE_DEPENDENCIES.md`
