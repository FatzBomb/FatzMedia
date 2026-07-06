# Completed Stage — 005B Device-Lost Recovery

Status: Complete
Completed: 2026-07-06

## Original Goal

Recover from device-lost/device-reset by rebuilding the necessary native presentation resources.

## What Changed

- **`NativeMediaPresentationCoordinator` now subscribes to `DiagnosticRouter.DiagnosticOccurred`**, filtering for `Level == Error && Category == "D3D11.DeviceLost"` (the signal Stage 005A raises from the D3D11 presentation layer).
- **The handler only sets a debounce guard** (`Interlocked.CompareExchange` on a new `_recoveryInProgress` field) and hands off to a new dedicated `PlaybackScheduler("DeviceLostRecovery")` worker thread — critical because the diagnostic fires synchronously from inside a `Present()` call, so rebuilding inline would tear down a swap chain mid-Present.
- **The recovery worker (`RunDeviceLostRecovery`)**: captures whether rendering was active, calls `StopRendering()`, then attempts up to 3 times (cancellable backoff: 250ms/500ms/1s via `cancellationToken.WaitHandle.WaitOne`) the existing rebuild sequence already proven by `DetachPresentationSurface`'s primary-surface path — `_presenter.CleanupResources()` → `EnsureD3D11()` → `RecreateAttachedSurfacesNoLock()` — all under the coordinator's existing `_lock`. This rebuilds the D3D11 device, the primary swap chain/DirectComposition target, and every secondary output from the HWNDs/dimensions already retained in the `_surfaces` dictionary — no WPF adapter involvement, no new windows created.
- **On success**: restarts rendering if it was previously active, re-presents the current composition, and logs an `Info`/`"D3D11.DeviceLost"` diagnostic.
- **On exhaustion (3 failed attempts)**: raises a new terminal `Error`/`"D3D11.DeviceLost.RecoveryFailed"` diagnostic and stops — a permanently-lost/removed GPU does not spin retries forever.
- **`Dispose()` unsubscribes from `DiagnosticRouter.DiagnosticOccurred`** and disposes the new recovery scheduler; the backoff wait is cancellable so shutdown during an in-flight recovery attempt does not block.
- **As a direct follow-through of a Stage 005A deferred item**, device-lost detection was also extended (in the same work batch, committed separately) to the `Commit()`/`ResizeBuffers()` call sites that previously discarded their HRESULT results: `D3D11NativeHwndSwapChainBackend.ResizeCore`, `D3D11DirectCompositionSingleTargetBackend.ResizeCore` (both base and overlay chains), and `D3D11DirectCompositionTarget`'s three `Commit()` call sites (`Create`, `SetContentSize`, per-frame `Commit()`). These reuse existing per-instance latches (or a new one added to `D3D11DirectCompositionTarget`) so the same loss episode is not reported twice, and preserve existing control flow/return values exactly (detection-only, same principle as Stage 005A).

## Files Changed

**New files:**
- (none for the recovery orchestrator itself — added to the existing `NativeMediaPresentationCoordinator.cs`)
- `FatzBombMedia.MediaControl.Tests/D3D11DeviceLostClassifierTests.cs` (12 tests, added alongside — see Behavior Notes)
- `docs/roadmap/005C_BACKLOG_DecoderDeviceLostRecovery.md` (new deferred stage, added alongside — see Deferred / Follow-Up Items)

**Modified files:**
- `FatzBombMedia.MediaControl/Core/Graphics/NativeMediaPresentationCoordinator.cs` — device-lost recovery orchestrator: diagnostic subscription, debounce guard, dedicated recovery worker, retry/backoff, terminal-failure diagnostic, Dispose cleanup.
- `FatzBombMedia.MediaControl/Core/Graphics/D3D/Presentation/NativeHwnd/D3D11NativeHwndSwapChainBackend.cs` — classifies `ResizeBuffers` HRESULT, reuses `_deviceLostReported`.
- `FatzBombMedia.MediaControl/Core/Graphics/D3D/Presentation/DirectComposition/D3D11DirectCompositionSingleTargetBackend.cs` — classifies base + overlay `ResizeBuffers` HRESULT via a new `ReportResizeIfDeviceLost` helper, reuses `_baseDeviceLostReported`/`_overlayDeviceLostReported`.
- `FatzBombMedia.MediaControl/Core/Graphics/D3D/Presentation/DirectComposition/D3D11DirectCompositionTarget.cs` — adds a new `_deviceLostReported` latch and a `CommitCore` helper used by all three `Commit()` call sites, reset in `Dispose()`.
- `docs/roadmap/STAGE_MANIFEST.md` — registers the new Stage 005C entry (unrelated stages untouched).

## Validation

- `dotnet build FatzBombMedia.sln -c Release` (TreatWarningsAsErrors=true) — 0 warnings, 0 errors.
- `dotnet test` — 137 passed, 0 failed (125 existing + 12 new classifier tests), no regressions.
- WPF adapter boundary check: no new Core renderer/D3D11/pipeline/internal-Cdg namespaces pulled into `FatzBombMedia.MediaControl.Wpf`.
- Manual smoke test (normal playback across video/CDG/image/sidecar background/secondary output/resize/fullscreen, plus a simulated device-lost recovering presentation and a permanent loss stopping after 3 attempts instead of looping) is still outstanding — could not be automated; see `docs/VALIDATION.md`.

## Behavior Notes

- Recovery only covers the presentation device (swap chains, DirectComposition target, secondary outputs). The FFmpeg-owned hardware-decode device (attached via `D3D11DeviceResources.InitializeFromNativeDevice`) is a separate device and is NOT recovered by this stage — explicitly deferred to the new Stage 005C.
- No WPF adapter changes — recovery reuses the same HWNDs already retained in Core; the adapter never knows a device-lost/recovery happened.
- Bounded retry (3 attempts, 250ms/500ms/1s backoff) — a permanently-lost GPU stops retrying rather than looping forever.
- `D3D11DeviceLostClassifierTests.cs` tests only the pure/deterministic classification logic (`IsDeviceLost`/`Describe`); the actual Present/ResizeBuffers/Commit-against-real-hardware integration and the recovery orchestration itself cannot be unit-tested (requires a live GPU/driver) and remain manual-smoke-only.

## Deferred / Follow-Up Items

- **Stage 005C (Decoder Device-Lost Recovery)** — new backlog stage added at `docs/roadmap/005C_BACKLOG_DecoderDeviceLostRecovery.md` for the FFmpeg hardware-decode device, explicitly out of this stage's scope.
- **Manual smoke test** of a simulated device-lost condition (GPU adapter disable/re-enable or driver TDR) has not yet been performed/confirmed — this is the one remaining validation gap for both Stage 005A and 005B.
