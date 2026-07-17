<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: COMPLETED.

# Completed Stage 012 — Core/Windows Platform Boundary Cleanup

## Status

Completed 2026-07-17. Core now owns DI composition and no longer leaks internals to Windows; Windows supplies only platform-specific services through explicit contracts.

## Why This Exists

The Windows project (`FatzBomb.MediaControl.Windows`) previously held `VideoPipeline`, depended on `InternalsVisibleTo` from Core, and accessed Core internals directly. This made the Core/Windows boundary porous and prevented clean packaging of Core as an OS-agnostic engine. This stage formalizes the boundary so Core can be consumed without Windows-specific project references.

## Delivered

- **Core owns DI composition** via `MediaControlServiceProvider.Create()` — Core composes its own `IServiceProvider` from `MediaPlayerConfiguration` and `IMediaControlPlatformServices`. Windows no longer owns the container.
- **`IMediaControlPlatformServices`** contract in Core (`FatzBomb.MediaControl/Api/IMediaControlPlatformServices.cs`) — Windows implements `WindowsMediaControlPlatformServices.AddPlatformServices()` and `EnsureNativeLibrariesLoaded()`.
- **`IVideoPipelinePlatformServices`** contract in Core (`FatzBomb.MediaControl/Core/Abstractions/IVideoPipelinePlatformServices.cs`) — Windows implements `WindowsVideoPipelinePlatformServices`.
- **`VideoPipeline` moved into Core** (`FatzBomb.MediaControl/Pipeline/VideoPipeline.cs` and `VideoPipelineFactory.cs`) — removed from `FatzBomb.MediaControl.Windows`. Windows-specific DWM timing and Direct3D device-loss checks are supplied through `IVideoPipelinePlatformServices`.
- **`InternalsVisibleTo` for Windows removed** from Core — `FatzBomb.MediaControl.csproj` now grants internals only to Tests and Rendering.Direct3D11.
- **`check-core-boundary.ps1` updated** — enforces no `InternalsVisibleTo` for Windows, verifies project-reference direction, and scans Core source for platform/renderer/UI/PInvoke references.
- **Shared platform helpers** — `AudioDeviceChangeDetector`, `BassAudioOutputDevices`, `MidiLyricExtractor`, `DiagnosticRouter`, and `DisplayRefreshInterval` now expose their narrow platform-facing APIs without exposing engine implementation state.

## Preserved Behavior

- Video playback, seeking, looping, A/V sync, decode device recovery, and hardware decode detection are unchanged — the moved `VideoPipeline` is functionally identical; platform services are injected rather than accessed via `InternalsVisibleTo`.
- WPF adapter boundary is unchanged — `check-wpf-boundary.ps1` passes.
- All existing Core service resolution paths are preserved through the new `MediaControlServiceProvider`.

## Verification

- `dotnet build -c Release FatzBomb.MediaControl.sln --no-restore` — zero warnings.
- `dotnet test` — 275 tests pass.
- `scripts/check-core-boundary.ps1` passes — no InternalsVisibleTo for Windows, no Windows/renderer/UI/PInvoke references in Core source.
- `scripts/check-wpf-boundary.ps1` passes.
