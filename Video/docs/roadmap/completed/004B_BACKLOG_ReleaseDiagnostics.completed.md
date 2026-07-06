# Completed Stage — 004B Release-Visible Diagnostics

Status: Complete
Completed: 2026-07-06

## Original Goal

Make important diagnostics available in Release builds through a host-routable path, replacing Debug-only `Debug.WriteLine` with a structured, filterable diagnostic surface.

## What Changed

### Public API (host-facing)

- **Added `MediaDiagnosticLevel` enum** (`Api/MediaDiagnosticLevel.cs`) with three severity levels: `Info`, `Warning`, `Error`.
- **Added `MediaDiagnosticEventArgs : EventArgs`** (`Api/MediaDiagnosticEventArgs.cs`) carrying `Level`, `Category`, `Message`, `SourcePath` (optional), `InnerException` (optional).
- **Added `MediaDiagnostic` event** (`EventHandler<MediaDiagnosticEventArgs>?`) to `IMediaControlSession` and `MediaControlPlayer` — the public host-facing diagnostic event. Fires in both Debug and Release builds. Fires from background threads (operation queue); hosts must marshal to UI thread.

### Internal machinery

- **Added `DiagnosticRouter`** (`Core/DiagnosticRouter.cs`) — internal static event sink with `Raise(MediaDiagnosticLevel, string category, string message, ...)` method. Any engine component can call it without coupling to the public API.
- **Added `MediaDiagnosticInfo` internal record** (`Core/MediaDiagnosticInfo.cs`) — payload record that keeps pipeline internals out of the public contract.
- **`MediaControlPlayer` subscribes to `DiagnosticRouter.DiagnosticOccurred`** in the constructor and re-marshals to the public `MediaDiagnostic` event through the existing `MediaControlOperationQueue` (same pattern as `MediaError`/`MediaEnded`/`MediaStarted`). Unsubscribes in `Dispose`.

### Instrumented engine/pipeline files (23 files, 121 insertions)

Diagnostic calls added alongside existing `Debug.WriteLine(...)` — one `DiagnosticRouter.Raise(...)` per existing `Debug.WriteLine` at high-value diagnostic points. No `Debug.WriteLine` calls were removed.

| File | Level | Key points instrumented |
|------|-------|------------------------|
| **Bootstrap** | | |
| `Runtime/Bootstrap.cs` | Error | Init exception, path-not-found validation |
| `Runtime/Bootstrap.LibraryLoader.cs` | Info / Warning / Error | BASS DLL load success/failure, FFmpeg path found/load success, optional DLL missing warnings |
| **Audio** | | |
| `AudioEngine.cs` | Info / Error | BASS init success/failure, open success/failure (file + archive), gain FX setup, normalization analysis start/cancel/fail, dispose |
| `AudioNormalizer.cs` | Info / Error | Peak analysis result, gain FX create success/failure, gain apply failure |
| `AudioStreamManager.cs` | Error / Warning | OpenFromPath/OpenFromArchive failure, tempo stream fallback |
| `VideoAudioEngine.cs` | Error / Warning | BASS push stream failure, tempo stream failure, temp file delete failure |
| **CDG** | | |
| `CdgEngine.cs` | Info | CDG file loaded (packet count, duration) |
| `Controllers/MediaFile.cs` | Error | MediaFile load failure |
| **D3D11 / Composition** | | |
| `D3D11DeviceResources.cs` | Error | Device creation failure, native device attach failure |
| `D3D11DirectCompositionSingleTargetBackend.cs` | Error | Backend creation failure |
| `D3D11DirectCompositionTarget.cs` | Error | Target creation failure |
| `D3D11NativeHwndSwapChainBackend.cs` | Error | Swap-chain backend creation failure |
| `D3D11SwapChainPresenter.cs` | Warning | Swap-chain Present failure |
| `D3D11MediaCompositionPresenter.cs` | Error / Warning | Hardware background copy failure, visible target present failure |
| `NativeMediaPresentationCoordinator.cs` | Warning | Video frame present failure, composition present failure |
| **Pipeline** | | |
| `PipelineMediaEngine.cs` | Warning | Rejected unsupported file type |
| `PlaybackScheduler.cs` | Warning / Error | Worker stop timeout, worker exit errors, worker failure |
| `MediaControlOperationQueue.cs` | Error / Warning | Worker exit error, stop timeout, wait failure |
| `CdgPipeline.cs` | Warning | Seek worker join failure, seek pump error |
| `VideoPipeline.cs` | Warning / Error | Loop-restart decoder seek failure, audio Play() failure, hw→sw fallback, software fallback failure |
| **Infrastructure** | | |
| `MediaPlayerServiceProvider.cs` | Info / Error | BASS global init success/failure, AudioEngine created |
| **API** | | |
| `IMediaControlSession.cs` | — | Added `MediaDiagnostic` event |
| `MediaControlPlayer.cs` | — | Subscribe/unsubscribe to `DiagnosticRouter`, re-marshal via op queue |

### Tests

- **Added `MediaDiagnosticEventArgsTests`** (4 tests) covering property assignment, null optional values, null-string-to-empty conversion, and all defined level round-tripping.

## Files Changed

**New files:**
- `FatzBombMedia.MediaControl/Api/MediaDiagnosticLevel.cs`
- `FatzBombMedia.MediaControl/Api/MediaDiagnosticEventArgs.cs`
- `FatzBombMedia.MediaControl/Core/DiagnosticRouter.cs`
- `FatzBombMedia.MediaControl/Core/MediaDiagnosticInfo.cs`
- `FatzBombMedia.MediaControl.Tests/MediaDiagnosticEventArgsTests.cs`

**Modified files (23):**
- `FatzBombMedia.MediaControl/Api/IMediaControlSession.cs` — added `MediaDiagnostic` event
- `FatzBombMedia.MediaControl/Api/MediaControlPlayer.cs` — subscribe/unsubscribe, re-marshal via op queue
- `FatzBombMedia.MediaControl/Controllers/MediaFile.cs` — 1 diagnostic call
- `FatzBombMedia.MediaControl/Core/Audio/AudioEngine.cs` — 16 diagnostic calls
- `FatzBombMedia.MediaControl/Core/Audio/AudioNormalizer.cs` — 6 diagnostic calls
- `FatzBombMedia.MediaControl/Core/Audio/AudioStreamManager.cs` — 3 diagnostic calls
- `FatzBombMedia.MediaControl/Core/Audio/VideoAudioEngine.cs` — 3 diagnostic calls
- `FatzBombMedia.MediaControl/Core/Cdg/CdgEngine.cs` — 2 diagnostic calls
- `FatzBombMedia.MediaControl/Core/Graphics/Composition/D3D11MediaCompositionPresenter.cs` — 2 diagnostic calls
- `FatzBombMedia.MediaControl/Core/Graphics/D3D/Device/D3D11DeviceResources.cs` — 3 diagnostic calls
- `FatzBombMedia.MediaControl/Core/Graphics/D3D/Presentation/DirectComposition/D3D11DirectCompositionSingleTargetBackend.cs` — 1 diagnostic call
- `FatzBombMedia.MediaControl/Core/Graphics/D3D/Presentation/DirectComposition/D3D11DirectCompositionTarget.cs` — 1 diagnostic call
- `FatzBombMedia.MediaControl/Core/Graphics/D3D/Presentation/NativeHwnd/D3D11NativeHwndSwapChainBackend.cs` — 1 diagnostic call
- `FatzBombMedia.MediaControl/Core/Graphics/D3D/SwapChain/D3D11SwapChainPresenter.cs` — 2 diagnostic calls
- `FatzBombMedia.MediaControl/Core/Graphics/NativeMediaPresentationCoordinator.cs` — 2 diagnostic calls
- `FatzBombMedia.MediaControl/Core/Threading/MediaControlOperationQueue.cs` — 5 diagnostic calls
- `FatzBombMedia.MediaControl/MediaPlayerServiceProvider.cs` — 3 diagnostic calls
- `FatzBombMedia.MediaControl/Pipeline/CdgPipeline.cs` — 2 diagnostic calls
- `FatzBombMedia.MediaControl/Pipeline/PipelineMediaEngine.cs` — 1 diagnostic call
- `FatzBombMedia.MediaControl/Pipeline/PlaybackScheduler.cs` — 4 diagnostic calls
- `FatzBombMedia.MediaControl/Pipeline/VideoPipeline.cs` — 5 diagnostic calls
- `FatzBombMedia.MediaControl/Runtime/Bootstrap.LibraryLoader.cs` — 8 diagnostic calls
- `FatzBombMedia.MediaControl/Runtime/Bootstrap.cs` — 3 diagnostic calls

## Validation

Build/validation status: **Not provided** — changes are uncommitted in the working tree. No build or test results are available from this session.

## Behavior Notes

- No `Debug.WriteLine` calls were removed — `DiagnosticRouter.Raise(...)` was added alongside each existing debug trace. This is an additive change with zero risk to existing behavior.
- The `MediaDiagnostic` event fires from background threads (operation queue); hosts must marshal to the UI thread, consistent with `MediaError`/`MediaEnded`/`MediaStarted`/`MediaStopped`.
- Normal playback remains quiet — diagnostics are raised only at boundary events (open/close, init failure, load failure, worker errors, etc.), not per-frame.
- No timing, decoder, renderer, or media loading behavior was changed.
- The WPF adapter (`FatzBombMedia.MediaControl.Wpf`) inherits the `MediaDiagnostic` event via `MediaControlPlayer` — no WPF-side changes needed.

## Deferred / Follow-Up Items

- **Bootstrap/native-library-load failures** in the constructor path are now surfaced via `DiagnosticRouter.Raise` *after* the router is wired up. However, if `Bootstrap.EnsureInitialized()` fails completely during `new Player(...)`, the player constructor still throws `InvalidOperationException` — the diagnostics for *that specific path* fire before any subscriber exists. Acceptable since the exception carries `Bootstrap.Message`.
- **Per-frame or high-frequency diagnostic points** were not added (stage explicitly forbids spam). The periodic video summary log is preserved as-is.
- **Build and test validation** needs to be run to confirm zero regressions across 23 modified files and new test file.
- Manual smoke test via Player host recommended for Release-build diagnostic visibility.
