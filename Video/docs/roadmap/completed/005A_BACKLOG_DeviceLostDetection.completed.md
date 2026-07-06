# Completed Stage — 005A Device-Lost Detection

Status: Complete
Completed: 2026-07-06

## Original Goal

Detect GPU device-lost/device-removed/reset conditions and surface them clearly.

## What Changed

- **Added `D3D11DeviceLostClassifier`** (`Core/Graphics/D3D/Diagnostics/D3D11DeviceLostClassifier.cs`) — classifies `DXGI_ERROR_DEVICE_REMOVED` / `DXGI_ERROR_DEVICE_RESET` / `DXGI_ERROR_DEVICE_HUNG` / `DXGI_ERROR_DRIVER_INTERNAL_ERROR` vs. a generic present failure. Best-effort enriches the message with `ID3D11Device.DeviceRemovedReason` (via `swapChain.GetDevice<ID3D11Device>()`) when the classified code is specifically DeviceRemoved.
- **`D3D11SwapChainPresenter.Present`** (native HWND + DirectComposition base swap-chain, shared call site) now classifies Present failures. Device-lost failures raise `MediaDiagnosticLevel.Error` under a new diagnostic category `"D3D11.DeviceLost"` via the existing `DiagnosticRouter` → `MediaControlPlayer.MediaDiagnostic` event (no public API signature change — reused the existing diagnostic channel rather than adding a new `MediaErrorCode`, since `MediaErrorCode`/`IMediaErrorSource` is scoped to PipelineMediaEngine's open/decode failures, not renderer/GPU conditions). Generic present failures keep the prior `Warning`-level behavior unchanged.
- **`D3D11DirectCompositionSingleTargetBackend.PresentOverlayAndCommit`** (the DirectComposition overlay swap-chain Present) got the same classification treatment inline.
- **Reporting is latched per swap-chain-owning backend instance** (`_deviceLostReported` in `D3D11NativeHwndSwapChainBackend`; `_baseDeviceLostReported` / `_overlayDeviceLostReported` in `D3D11DirectCompositionSingleTargetBackend`) so a sustained failing present loop reports the condition once per loss episode instead of every frame. Latches reset on a subsequent successful Present and on `Cleanup()`.

## Files Changed

**New files:**
- `FatzBombMedia.MediaControl/Core/Graphics/D3D/Diagnostics/D3D11DeviceLostClassifier.cs`

**Modified files:**
- `FatzBombMedia.MediaControl/Core/Graphics/D3D/SwapChain/D3D11SwapChainPresenter.cs` — added device-lost classification + `ref bool deviceLostReported` latch parameter to `Present`.
- `FatzBombMedia.MediaControl/Core/Graphics/D3D/Presentation/NativeHwnd/D3D11NativeHwndSwapChainBackend.cs` — added `_deviceLostReported` field, reset in `Cleanup()`.
- `FatzBombMedia.MediaControl/Core/Graphics/D3D/Presentation/DirectComposition/D3D11DirectCompositionSingleTargetBackend.cs` — added `_baseDeviceLostReported` / `_overlayDeviceLostReported` fields, device-lost classification in `PresentOverlayAndCommit`, reset in `Cleanup()`.

## Validation

- `dotnet build FatzBombMedia.MediaControl\FatzBombMedia.MediaControl.csproj` (Debug) — 0 warnings, 0 errors.
- `dotnet build FatzBombMedia.sln -c Release` (TreatWarningsAsErrors=true) — 0 warnings, 0 errors.
- `dotnet test` — 125 passed, 0 failed, no regressions.
- WPF adapter boundary check: no new Core renderer/D3D11/pipeline/internal-Cdg namespaces pulled into `FatzBombMedia.MediaControl.Wpf` (only pre-existing `Core.Abstractions` / `Core.Configuration` usings present, both unrelated to this change).
- Manual smoke test (normal playback, and any reproducible simulated device-lost such as disabling/re-enabling the GPU adapter or a driver TDR) is still outstanding — could not be automated; see `docs/VALIDATION.md`.

## Behavior Notes

- Detection only, per stage scope — no recovery, no compositor rebuild, no swap-chain/sync-interval policy change.
- `Commit()` (DirectComposition) and `ResizeBuffers` (both native HWND and DirectComposition) call sites still discard their HRESULT results unchanged from before this stage — device-lost detection was intentionally scoped to Present call sites only.
- `D3D11VideoProcessorPresenter`'s `VideoProcessorBlt` failure path was left untouched (not a Present call, lower priority, out of scope for this stage).
- No WPF adapter changes — the new diagnostic surfaces automatically through the existing `MediaDiagnostic` event with no host-side code changes required.

## Deferred / Follow-Up Items

- **Stage 005B (Device-Lost Recovery)** is the next stage — it will consume the `"D3D11.DeviceLost"` diagnostic category (and the classifier) to drive actual device/swap-chain/compositor rebuild.
- **`Commit()` / `ResizeBuffers` HRESULT results remain unchecked** — a future stage could extend classification to those call sites if device-lost conditions are observed to surface there first in practice.
- **Manual smoke test** of a simulated device-lost condition has not yet been performed/confirmed.
