# FatzBombMedia — AGENTS.md

## Build & Run

```powershell
dotnet restore FatzBombMedia.sln
dotnet build FatzBombMedia.sln --no-restore

# Native DLLs required (not in repo). Put x64 BASS + FFmpeg DLLs in FatzBombMedia.Player/native/
# to run the Player:
dotnet run --project FatzBombMedia.Player

# Release mode treats warnings as errors
dotnet build -c Release FatzBombMedia.sln --no-restore
```

## Project Structure

| Project | Role | Key constraint |
|---------|------|---------------|
| `FatzBombMedia.MediaControl` (Core) | UI-agnostic engine — audio/video/Cdg, D3D11 composition, swap chains, final presentation | No WPF references. No `System.Windows.*`, `ImageSource`, `Dispatcher`, etc. |
| `FatzBombMedia.MediaControl.Wpf` (Adapter) | WPF interop only — HWND lifecycle, `MediaSurface` control, `Player : MediaControlPlayer` | Must NOT reference Core renderer, pipeline, internal Cdg, video-frame, or D3D11 types |
| `FatzBombMedia.Player` (Host) | WPF validation host — exercises Core through WPF adapter | CommunityToolkit.Mvvm, no production use |

## LangVersion & Runtime Features

- **C# 14** (`LangVersion 14.0`) — `field` keyword in auto-property accessors, primary constructors on non-record types, collection expressions all valid
- **`System.Threading.Lock`** (not `Monitor`) — used throughout pipeline for synchronization. This is the new explicit `Lock` type from .NET 9+, not the legacy `lock(obj)` pattern.
- **x64-only** — `PlatformTarget=x64` on all projects. Do not change.
- **AssemblyName ≠ ProjectName** — Core assembly is `FatzBombMedia.MediaControl`, but NuGet package ID is `FatzBombMedia.MediaControl.Core`
- **Packable** — Core project generates a NuGet package on build (`GeneratePackageOnBuild=true`)

## Architecture Rules

- **Adapters own zero rendering** — `MediaSurface` and `SecondaryPresentationWindow` create HWND surfaces and pass `MediaPresentationSurfaceDescriptor` to Core. They must never reference pipelines, decoders, frame data, internal Cdg state, or D3D11 resources.
- **`Player : MediaControlPlayer`** in WPF project inherits Core commands, properties, events automatically. WPF-specific additions are limited to `ImageSource`→`MediaInputSource` conversions.
- **`.ConfigureAwait(false)` required** in all async pipeline code. Host app marshals events to UI thread explicitly.

## Threading Gotchas (Easy to Miss)

- Synchronous `new Player(...)` blocks the calling thread — `Bootstrap.EnsureInitialized()` loads BASS + FFmpeg DLLs synchronously via `LoadLibraryW`. Prefer the async factory from UI code.
- Synchronous `Player.Pause()` / `Player.Stop()` block the calling thread — `PlaybackScheduler.StopAndWait()` calls `Task.Wait(2000)` on decode and presentation schedulers. Prefer async control APIs from UI code.
- `VideoPipeline.Open(path)` blocks the calling thread — creates decoder, opens audio engine, decodes first frame synchronously.
- `MediaEnded` / `MediaStopped` events fire from background threads (BASS callback / presentation loop). Handlers must marshal to UI thread.
- Seek is correctly non-blocking — runs on dedicated background seek threads with latest-wins pump.

## Key Entrypoints

| Concern | Location |
|---------|----------|
| Public API surface | `FatzBombMedia.MediaControl.Wpf.Player` (extends `MediaControlPlayer : IMediaControlSession`) |
| WPF UI control | `FatzBombMedia.MediaControl.Wpf.MediaSurface` (a `Grid` subclass with native HWND child) |
| DI composition | `MediaPlayerServiceProvider.Create()` — singleton DI, no external overrides |
| Pipeline orchestrator | `Pipeline.PlaybackCoordinator` (internal) — wires AudioPipeline + CdgPipeline + VideoPipeline |
| Native lib loader | `Bootstrap.EnsureInitialized()` (internal) — load-once, synchronous, populates `FileTypes` |

## Verification

No tests or CI exist. Only verification paths:

1. `dotnet build` succeeds (no warnings in Release)
2. Manual smoke test via Player (see `docs/VALIDATION.md` for 15-item checklist)
3. WPF adapter boundary check — verify the Wpf project's `using` directives against `FatzBombMedia.MediaControl.Core.*` namespaces that are renderer/pipeline/internal Cdg/D3D11 (should be none)

## Docs That Matter

- `docs/CURRENT_ARCHITECTURE.md` — ownership boundaries and runtime flow
- `docs/VALIDATION.md` — smoke test checklist
- `docs/ProjectAnalysisRoadmap [2026-06-30].md` — active staged roadmap and validation criteria
