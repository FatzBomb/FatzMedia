# FatzMedia.Video

FatzMedia.Video is the video engine of FatzBomb Entertainment's FatzMedia: a UI-agnostic Core, renderer and platform
packages, and UI adapters for WPF, Avalonia and WinForms, with a test player for each. Audio comes from
FatzMedia.Audio (`..\Audio`, the native `fatzaudio` library), which is the only audio engine, and it also draws
CD+G and MIDI/KAR karaoke. Web media (YouTube links) plays through a web control built into this package set. User guide:
`docs/site` (DocFX).

## Projects

| Project | Purpose |
|---|---|
| `FatzMedia.Video` | Portable Core playback, commands, media state, sidecar detection, audio/video/Cdg, retained composition models, and platform/renderer contracts. |
| `FatzMedia.Video.Direct3D11` | Direct3D 11 renderer backend, GPU composition, swap-chain, and presentation implementation. |
| `FatzMedia.Video.Software` | Portable deterministic software renderer — in-memory premultiplied BGRA8 composition with no GPU or window. |
| `FatzMedia.Video.Windows` | Windows native-library loading, endpoint integration, timing, and hardware-decode policy. |
| `FatzMedia.Video.Windows.Direct3D11` | Convenience integration bundle pairing Windows services with the current renderer. |
| `FatzMedia.Video.Vulkan` | Preview Vulkan renderer backend (offscreen clear and background presentation into a frame sink). |
| `FatzMedia.Video.Linux` | Linux platform services: host-supplied FFmpeg shared-library loading, precision waits, software decode. |
| `FatzMedia.Video.Wpf` | WPF adapter for control/HWND lifecycle interop and WPF-specific input conversions. It receives platform composition from the host and owns no renderer, pipeline, layer, timing, or final presentation logic. |
| `FatzMedia.Video.Avalonia` | Avalonia adapter: composited `MediaSurface` (no native window; GPU-drawn on Avalonia's render thread, with a frame-sink bitmap path as the automatic fallback), `KaraokeSurface`, `BackgroundSurface` and bitmap conversions. |
| `FatzMedia.Video.WinForms` | WinForms adapter: HWND `MediaSurface` attached as a presentation surface (primary, or a second screen). |
| `FatzMedia.Video.WebView2` | Web media (YouTube, stage 060A) in a Windows web view: `WebView2MediaPlayer`, the WPF `WebMediaView` and the WinForms `WebMediaControl`. Attach its player to a `PlayerCore` and `Load("https://youtu.be/...")` plays the link. See `docs/site/articles/playback/web-media.md`. |
| `FatzMedia.Video.WebView2.Avalonia` | The same for Avalonia, on `NativeWebView` (`Avalonia.Controls.WebView`): `AvaloniaWebMediaPlayer` and `WebMediaView`. Verified on Windows only. |
| `FatzMedia.Video.Sample.Player.Shared` | The test players' shared, UI-agnostic view model (`PlayerViewModel`) and command line; hosts supply UI thread, dialogs and engine. |
| `FatzMedia.Video.Sample.Player` | WPF test player: exercises the Core engine through the WPF adapter. |
| `FatzMedia.Video.Sample.Player.Avalonia` | Cross-platform (Windows + Linux) Avalonia test player: software, Vulkan or Direct3D11 renderer, 1-4 players side by side with CPU/memory/fps status lines. |
| `FatzMedia.Video.Sample.Player.WinForms` | WinForms test player at WPF feature parity: HWND `MediaSurface` with Direct3D11. |

Future UI integrations should be separate adapter projects using the same package style (see `docs/roadmap/future/062A_IDEA_MoreUiAdapters.md`).

## Current Rule

`FatzMedia.Video` owns media-session orchestration and the neutral output model.
The host composition root selects platform services and a renderer, while adapters
supply framework/native surfaces and route framework-specific controls or source
types into Core APIs.

Each adapter has its own `Player` class that derives from Core `PlayerCore` (`FatzMedia.Video.Wpf.Player`, and the Avalonia and WinForms ones). Core commands, properties, and events are inherited automatically; adapter-specific additions stay limited to conversions such as `ImageSource`/`Bitmap` to `MediaInputSource`. Load media with `Load` / `LoadAsync`: a file path, an archive entry or a YouTube address.

## Native DLLs

FFmpeg native DLLs are not included. The host developer supplies and
licenses them through predefined or custom native paths. The audio engine,
FatzAudio, is built from `Audio\` in this repository and copied to the app output;
no other audio library or licence is needed. For the test players, put the x64
FFmpeg DLLs in the player's own `native/` folder (`FatzMedia.Video.Sample.Player`,
`.Sample.Player.Avalonia` or `.Sample.Player.WinForms`). Other hosts (TriviaBomb, Kamujo)
use the untracked folder `redist\win-x64`, filled by `scripts\stage-natives.ps1` (it downloads nothing).
See `docs/NATIVE_DEPENDENCIES.md` for filenames, supported versions/RIDs, approved
sources, licensing cautions, package layout, and the update process.

## Active Docs

- `docs/README.md` (index), `docs/site` (user guide)
- `docs/roadmap/STAGE_MANIFEST.md`
- `docs/CURRENT_ARCHITECTURE.md`
- `docs/VALIDATION.md`
- `docs/NATIVE_DEPENDENCIES.md`
