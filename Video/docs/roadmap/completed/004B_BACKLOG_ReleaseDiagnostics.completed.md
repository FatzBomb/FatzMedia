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

## Follow-Up (2026-07-06 — Commit `b74c1a2`)

Four follow-up items completed after the initial stage was merged:

### 1. Host Subscription (`MainWindowViewModel`)
- **New file:** `FatzBombMedia.Player/ViewModels/DiagnosticLogEntry.cs` — `sealed record` with `Timestamp`, `Level`, `Category`, `Message`.
- **Modified:** `MainWindowViewModel.cs` — subscribes to `Player.MediaDiagnostic` and `Player.MediaError` events on player creation.
- **`DiagnosticLog`** (`ObservableCollection<DiagnosticLogEntry>`) — 200-entry FIFO buffer. Log entries from both `MediaDiagnostic` and `MediaError` events are routed through a common `HandleDiagnostic` method.
- **`StatusText`** updates for `Warning`/`Error` level entries: `"[{level}] {category}: {message}"`.
- **Cleanup:** Unsubscribes from both events in `Dispose()`. Uses `Application.Current.Dispatcher.Invoke` for cross-thread event marshaling (same pattern as existing UI-thread ops).

### 2. Dual-Emit Cleanup
- **~50 `Debug.WriteLine` lines removed** from the 23 files instrumented in the original stage.
- Each removal site kept its `DiagnosticRouter.Raise(...)` call — no diagnostic coverage was lost.
- Files cleaned: `AudioEngine.cs`, `AudioNormalizer.cs`, `AudioStreamManager.cs`, `VideoAudioEngine.cs`, `CdgEngine.cs`, `D3D11MediaCompositionPresenter.cs`, `D3D11DeviceResources.cs`, `D3D11DirectCompositionSingleTargetBackend.cs`, `D3D11DirectCompositionTarget.cs`, `D3D11NativeHwndSwapChainBackend.cs`, `D3D11SwapChainPresenter.cs`, `NativeMediaPresentationCoordinator.cs`, `MediaControlOperationQueue.cs`, `MediaPlayerServiceProvider.cs`, `CdgPipeline.cs`, `PipelineMediaEngine.cs`, `PlaybackScheduler.cs`, `VideoPipeline.cs`, `Bootstrap.LibraryLoader.cs`, `Bootstrap.cs`.

### 3. Missed Promotion Sites
5 additional sites in `MediaFile` and `MediaControls` that had `Debug.WriteLine` but no `DiagnosticRouter.Raise` were promoted:

| File | Site | Level |
|------|------|-------|
| `MediaFile.cs` | `PlayOnLoad`: "Failed to start playback" | Warning |
| `MediaFile.cs` | `PlayOnLoad` exception | Error |
| `MediaFile.cs` | `LoadAndPlay`: "Stop failed or returned false" | Warning |
| `MediaFile.cs` | `SetBackground`: "SetBackground failed" | Warning |
| `MediaControls.cs` | Error handling media end exception | Warning |

No new dual-emit sites were introduced — these replaced `Debug.WriteLine` directly with `DiagnosticRouter.Raise` (no carry-over `Debug.WriteLine`).

### 4. CdgEngine Dead Code Removal
- Removed `_lastRenderLogSecond` field (int) — was tracking the last-logged second for per-second frame invalidation debug output.
- Removed its initialization in `ResetInternal()`.
- Removed the per-second `Debug.WriteLine` guard block inside the frame invalidation path — the existing `DiagnosticRouter.Raise(...)` for CDG load events remains.

### Files Changed (Follow-Up)
- 1 new file: `DiagnosticLogEntry.cs`
- 23 modified files: `MainWindowViewModel.cs` + 22 Core files (20 Debug.WriteLine cleanups + 2 promotion-only files)
- Stats: 62 insertions, 113 deletions (net -51 lines)

### Validation
- **Not provided** — changes are from the committed follow-up. No build/test results available in this session.

### Behavior Notes
- Host subscription is entirely additive — no existing behavior changed.
- Dual-emit removal is safe: `DiagnosticRouter.Raise` exists at every former `Debug.WriteLine` site. No diagnostic loss.
- CdgEngine dead code removal is pure cleanup — `_lastRenderLogSecond` was a write-only field read only by a now-removed `Debug.WriteLine`. No behavior change.
- The WPF adapter remains untouched — all changes are in Core (DiagnosticRouter cleanup) and Player (host subscription).
