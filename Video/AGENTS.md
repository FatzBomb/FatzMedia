# FatzBombMedia — AGENTS.md

*Local repo rules. Augments the global `~/.config/opencode/AGENTS.md`; these repo
facts win where they conflict with global preferences.*

## Build & Run

```powershell
dotnet restore FatzBomb.MediaControl.sln
dotnet build FatzBomb.MediaControl.sln --no-restore

# Native DLLs required (not in repo). Put x64 BASS + FFmpeg DLLs in FatzBomb.MediaControl.Player/native/
# to run the Player. A bundled TimGM6mb.sf2 soundfont is in native/ for MIDI fallback.
# to run the Player:
dotnet run --project FatzBomb.MediaControl.Player

# Release mode treats warnings as errors
dotnet build -c Release FatzBomb.MediaControl.sln --no-restore
```

Shared build settings (LangVersion, Nullable, PlatformTarget, versioning, Release
warnings-as-errors) are centralized in the repo-root `Directory.Build.props`.

## Project Structure

| Project | Role | Key constraint |
|---------|------|---------------|
| `FatzBomb.MediaControl` (Core) | OS/UI-agnostic engine — playback, media state, audio/video/Cdg, composition models, platform/renderer contracts | Targets `net10.0`; no Win32, Vortice, WPF, renderer, or platform project references |
| `FatzBomb.MediaControl.Rendering.Direct3D11` | Direct3D 11 renderer backend — device, GPU composition, swap chains, subtitle rasterization, final presentation | References only public Core renderer contracts/models; no friend access |
| `FatzBomb.MediaControl.Rendering.Software` | Portable deterministic software renderer — in-memory premultiplied BGRA8 composition with no GPU or window | Targets `net10.0`; references Core only |
| `FatzBomb.MediaControl.Windows` | Windows platform composition — native-library loading, endpoint integration, hardware-decode policy | References Core only; renderer selection belongs to the host composition root |
| `FatzBomb.MediaControl.Windows.Direct3D11` | Convenience integration bundle | References Windows + Direct3D11 and exposes the current default platform factory |
| `FatzBomb.MediaControl.Wpf` (Adapter) | WPF interop — `MediaSurface`, `BackgroundSurface`, `KaraokeSurface`, and `Player : MediaControlPlayer` | Controls accept an `IMediaControlPlatformFactory`; project must NOT reference renderer, pipeline, internal Cdg, video-frame, or D3D11 types |
| `FatzBomb.MediaControl.Player` (Host) | WPF validation host — exercises Core through WPF adapter | CommunityToolkit.Mvvm, no production use |

## LangVersion & Runtime Features

- **C# 14** (`LangVersion 14.0`) — `field` keyword in auto-property accessors, primary constructors on non-record types, collection expressions all valid
- **Portable Core TFM** — `FatzBomb.MediaControl` and `FatzBomb.MediaControl.Rendering.Software` target `net10.0`; Windows, Direct3D11, WPF, Player, and tests target `net10.0-windows7.0`.
- **`System.Threading.Lock`** (not `Monitor`) — used throughout pipeline for synchronization. This is the new explicit `Lock` type from .NET 9+, not the legacy `lock(obj)` pattern.
- **Current Windows deployment** — Direct3D11, Windows, WPF, Player, and tests explicitly target x64 for the current native-media distribution. Core and the Software renderer are architecture-neutral; future platform packages must select their own supported runtime architectures.
- **Consistent identity** — Core PackageId == AssemblyName == RootNamespace == `FatzBomb.MediaControl` (the bare product name; the old `.Core` package suffix was dropped). Namespace root across the solution is `FatzBomb.MediaControl` (host is `FatzBomb.MediaControl.Player`).
- **Packable** — Core, Windows, and Rendering.Software packages are created explicitly with `dotnet pack -c Release` (the Software package also emits a snupkg); normal builds do not pack.

## Architecture Rules

- **Adapters own zero rendering** — `MediaSurface` and `SecondaryPresentationWindow` create HWND surfaces and pass `MediaPresentationSurfaceDescriptor` to Core. They must never reference pipelines, decoders, frame data, internal Cdg state, or D3D11 resources.
- **`Player : MediaControlPlayer`** in WPF project inherits Core commands, properties, events automatically. WPF-specific additions are limited to `ImageSource`→`MediaInputSource` conversions.
- **Platform composition is explicit** — `MediaControlPlayer` receives an `IMediaControlPlatformFactory`; Core composes its own services through `MediaControlServiceProvider`, while OS packages own native-library loading and `IMediaControlPlatformServices` registrations. `WindowsMediaControlPlatformFactory` now lives in `FatzBomb.MediaControl.Windows` and accesses Core only through public platform contracts.
- **Renderer choice is separate from OS choice** — hosts inject an `IMediaPresentationCoordinatorFactory` into the Windows/WPF composition chain. The Player host uses the `FatzBomb.MediaControl.Windows.Direct3D11` convenience bundle; the portable `FatzBomb.MediaControl.Rendering.Software` package offers the same seam through `SoftwarePresentationCoordinatorFactory`; future Vulkan/OpenGL/Metal renderers implement the same public Core contracts without changing Core playback, Windows services, or UI adapters.
- **`.ConfigureAwait(false)` required** in all async pipeline code. Host app marshals events to UI thread explicitly.
- **Before editing the Wpf adapter**, confirm no new `using` pulls a Core renderer / pipeline / internal-Cdg / D3D11 namespace into the adapter — that boundary is the repo's most important invariant (see Verification).
- **Large engine/pipeline files are expected here.** D3D11 composition, decoders, swap-chain/presentation, and Cdg code are legitimately large and cohesive — do **not** propose splitting them on line count alone (this exempts them from the global file-size trigger). Apply that trigger to *new* UI, view-model, service, and glue code, not to the existing low-level engine.

## Threading Gotchas (Easy to Miss)

- Synchronous `new Player(...)` blocks the calling thread — `Bootstrap.EnsureInitialized()` loads BASS + FFmpeg DLLs synchronously via `LoadLibraryW`. Prefer the async factory from UI code.
- Synchronous `Player.Pause()` / `Player.Stop()` block the calling thread — `PlaybackScheduler.StopAndWait()` calls `Task.Wait(2000)` on decode and presentation schedulers. Prefer async control APIs from UI code.
- `Player.Dispose()` is synchronous and waits behind admitted media operations before teardown; do not call it on the UI thread while a long-running load/control operation is active.
- `VideoPipeline.Open(path)` blocks the calling thread — creates decoder, opens audio engine, decodes first frame synchronously.
- `MediaEnded` / `MediaStopped` events fire from background threads (BASS callback / presentation loop). Handlers must marshal to UI thread.
- Seek is correctly non-blocking — runs on dedicated background seek threads with latest-wins pump.

## Key Entrypoints

| Concern | Location |
|---------|----------|
| Public API surface | `FatzBomb.MediaControl.Wpf.Player` (extends `MediaControlPlayer : IMediaControlSession`) |
| WPF UI control | `FatzBomb.MediaControl.Wpf.MediaSurface` (a `Grid` subclass with native HWND child) |
| DI composition | `MediaPlayerServiceProvider.Create()` — singleton DI, no external overrides |
| Pipeline orchestrator | `Pipeline.PlaybackCoordinator` (internal) — wires AudioPipeline + CdgPipeline + VideoPipeline |
| MIDI lyric extraction | `Core.Audio.MidiLyricExtractor` (internal) — extracts and groups karaoke lyrics from BASS MIDI markers |
| Karaoke rendering | `FatzBomb.MediaControl.Rendering.Direct3D11.Subtitle.SubtitleTextRenderer` (internal) — Direct2D/DWrite rasterization with wipe, scroll, and page-fade animation |
| Karaoke style config | `Core.Configuration.KaraokeTextStyle` — independent font/color/outline/shadow for karaoke lyrics |
| Sidecar/background resolver | `Controllers.MediaBackgroundResolver` (internal) — auto-detects image/video sidecars for non-video/image mains; image mains require explicit backgrounds |
| Software renderer entry | `FatzBomb.MediaControl.Rendering.Software.SoftwarePresentationCoordinatorFactory` — host-selectable portable renderer coordinator; `SoftwareCompositionRenderer` produces deterministic premultiplied BGRA8 output |
| Native lib loader | `FatzBomb.MediaControl.Windows.Bootstrap.EnsureInitialized()` (internal) — load-once, synchronous, populates `FileTypes` |

## Code Navigation

If `graphify-out/graph.json` exists in this repo, query Graphify first for
structural / code-flow / entrypoint questions before the index or grep (per global
rules). The pipeline/adapter boundaries here are exactly the kind of thing the
graph answers well.

## WPF / XAML / MVVM style (Player host)

Applies to `FatzBomb.MediaControl.Player` (and any WPF UI). **Core stays UI-agnostic — none
of this applies there.**

Use MVVM via CommunityToolkit.Mvvm:
- `ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`, async relay commands
- view models registered through the host's DI where applicable
- bindings over code-behind; resource dictionaries for shared styles
- async commands for long-running work; validation/status messages over silent failure

UI-thread rules (this repo specifically):
- `MediaEnded` / `MediaStopped` fire from **background** threads — marshal to the UI
  thread in handlers before touching view state.
- Prefer the async factory and async control APIs from UI code; the synchronous
  `new Player(...)`, `Pause()`, `Stop()`, and `VideoPipeline.Open(path)` block the
  calling thread (see Threading Gotchas).

Code-behind allowed only for pure view behavior, focus, animation/view events, or
HWND/interop hooks (e.g. `MediaSurface`). No business logic in code-behind or XAML.

## Testing

A test project exists at `FatzBomb.MediaControl.Tests` with xUnit. Run `dotnet test`.

Cover **pure** logic only (file-type detection / `FileTypes`, parsing, sorting, state
transitions in the pipeline, karaoke timing, viewport geometry, style normalization) with xUnit.
Do not unit-test media / D3D11 / threading / native-DLL paths — verify those manually.

Test files that mutate the static `FileTypes.Audio`/`FileTypes.Video` sets must use the
`[Collection("File type registry")]` attribute to prevent parallel test contamination.

## Verification

1. `dotnet build` succeeds (no warnings in Release)
2. `dotnet test` passes (464+ tests covering pure logic)
3. Manual smoke test via Player (see `docs/VALIDATION.md` for checklist)
4. WPF adapter boundary check — verify the Wpf project's `using` directives against `FatzBomb.MediaControl.*` namespaces that are renderer/pipeline/internal Cdg/D3D11 (should be none)

## Docs That Matter

- `docs/CURRENT_ARCHITECTURE.md` — ownership boundaries and runtime flow
- `docs/VALIDATION.md` — smoke test checklist
- `docs/NATIVE_DEPENDENCIES.md` — host-owned BASS/FFmpeg versions, paths, licensing, and publish-smoke policy
- `docs/PRIVATE_NATIVE_RUNNER.md` — dedicated self-hosted native smoke runner setup
- `docs/roadmap/STAGE_MANIFEST.md` — active staged roadmap and validation criteria
