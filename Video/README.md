# FatzMedia.Video

FatzMedia.Video is a media-control solution with a UI-agnostic Core engine, a strict WPF adapter, and a small WPF validation player.

## Projects

| Project | Purpose |
|---|---|
| `FatzMedia.Video` | Portable Core playback, commands, media state, sidecar detection, audio/video/Cdg, retained composition models, and platform/renderer contracts. |
| `FatzMedia.Video.Direct3D11` | Direct3D 11 renderer backend, GPU composition, swap-chain, and presentation implementation. |
| `FatzMedia.Video.Software` | Portable deterministic software renderer — in-memory premultiplied BGRA8 composition with no GPU or window. |
| `FatzMedia.Video.Windows` | Windows native-library loading, endpoint integration, timing, and hardware-decode policy. |
| `FatzMedia.Video.Windows.Direct3D11` | Convenience integration bundle pairing Windows services with the current renderer. |
| `FatzMedia.Video.Vulkan` | Preview Vulkan renderer backend (offscreen clear and background presentation into a frame sink). |
| `FatzMedia.Video.Linux` | Linux platform services: host-supplied BASS/FFmpeg shared-library loading, precision waits, BASS device monitoring, software decode. |
| `FatzMedia.Video.Wpf` | WPF adapter for control/HWND lifecycle interop and WPF-specific input conversions. It receives platform composition from the host and owns no renderer, pipeline, layer, timing, or final presentation logic. |
| `FatzMedia.Video.Avalonia` | Avalonia adapter: composited `MediaSurface` over the frame-sink contract and bitmap conversions. |
| `FatzMedia.Video.WinForms` | WinForms adapter: HWND `MediaSurface` attached as a presentation surface (primary, or a second screen). |
| `FatzMedia.Video.Sample.Player.Shared` | The test players' shared, UI-agnostic view model (`PlayerViewModel`) and command line; hosts supply UI thread, dialogs and engine. |
| `FatzMedia.Video.Sample.Player` | WPF validation host used to exercise the Core engine through the WPF adapter (the reference player). |
| `FatzMedia.Video.Sample.Player.Avalonia` | Cross-platform (Windows + Linux) Avalonia test player at WPF feature parity: software, Vulkan or Direct3D11 renderer, 1-4 players side by side with CPU/memory/fps status lines. |
| `FatzMedia.Video.Sample.Player.WinForms` | WinForms test player at WPF feature parity: HWND `MediaSurface` with Direct3D11. |

Future UI integrations should be separate adapter projects using the same package style, for example `FatzMedia.Video.WinUI`, `FatzMedia.Video.Avalonia`, and `FatzMedia.Video.WinForms`.

## Current Rule

`FatzMedia.Video` owns media-session orchestration and the neutral output model.
The host composition root selects platform services and a renderer, while adapters
supply framework/native surfaces and route framework-specific controls or source
types into Core APIs.

The WPF adapter exposes `FatzMedia.Video.Wpf.Player`, which derives from Core `PlayerCore`. Core commands, properties, and events are inherited automatically; WPF-specific additions should stay limited to conversions such as `ImageSource`/`BitmapSource` to `MediaInputSource`.

## Native DLLs

BASS and FFmpeg native DLLs are not included. The host developer supplies and
licenses them through predefined or custom native paths. For local Player testing,
put x64 native DLLs in `FatzMedia.Video.Sample.Player/native/`. See
`docs/NATIVE_DEPENDENCIES.md` for filenames, supported versions/RIDs, approved
sources, licensing cautions, package layout, and the update process.

## Active Docs

- `docs/roadmap/STAGE_MANIFEST.md`
- `docs/CURRENT_ARCHITECTURE.md`
- `docs/VALIDATION.md`
- `docs/NATIVE_DEPENDENCIES.md`
