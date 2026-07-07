# Completed Stage — 005C Decoder Device-Lost Recovery

Status: Complete
Completed: 2026-07-06

## Original Goal

Detect and recover the FFmpeg hardware-decode D3D11 device when it is lost/removed/reset, separately from the presentation-device recovery already implemented in Stage 005B.

## What Changed

- **Added a new `D3D11DeviceLostClassifier.IsDeviceLost(IntPtr d3d11DevicePointer)` overload** that best-effort wraps the raw FFmpeg-owned native `ID3D11Device` pointer via `Marshal.AddRef` + Vortice wrapper + `ID3D11Device.DeviceRemovedReason`. Used by the decode side, which holds only the unmanaged pointer (not a Vortice wrapper). Returns false for a null/zero pointer and for an unwrappable pointer, so it never produces a false positive.
- **`VideoDecoder` now counts consecutive hardware-path decode failures** (from `avcodec_receive_frame` hard errors and `av_hwframe_transfer_data` failures) via a new `_consecutiveHardwareDecodeFailures` field, resets it on any successful frame, and exposes `IsHardwareDecodeRepeatedlyFailing` (threshold = 3). `Close()` resets the counter.
- **`VideoPipeline` now has a dedicated `PlaybackScheduler("DecoderDeviceLostRecovery")` worker** that runs `RunDecoderDeviceLostRecovery` on device loss, debounced via `Interlocked.CompareExchange` on a new `_decoderRecoveryInProgress` field. Detection runs in the decode loop via `CheckForHardwareDecodeDeviceLoss()`, which only triggers when the decoder's hardware path is repeatedly failing AND the device-removed reason confirms loss (avoids falling back on a bad stream).
- **The recovery worker performs a runtime software-decode fallback**: `SwapVideoDecoder(VideoDecodingMethod.Software, path)` reuses the proven seek stop-reposition-restart dance (serialized via `_transportGate`) to replace the live decoder with a software one at the current transport position. Audio is deliberately left running untouched — for CDG karaoke, audio + CDG overlay are uninterrupted; a karaoke video file degrades to software but keeps playing. Bumping `_generation` drops in-flight lost-device frames, which is also the fix for the 005B orphaned-context risk: no more lost-device hardware frames means the presentation side stops re-adopting the dead device, breaking the feedback loop.
- **After software fallback, a slow (5s) background re-probe** (`ReprobeAndRestoreHardware` → `HardwareDecodeDeviceAvailable`) opens a throwaway hardware-required decoder; when the GPU returns, it swaps back to hardware and logs restoration. Loading a new file restores hardware automatically via the normal `Open()` path (verified: `FromConfiguration` → `Auto`).
- **Bounded retry**: 3 attempts, 500/1000/2000ms cancellable backoff (heavier than 005B's 250/500/1000ms because decode reopen reopens the demuxer + re-inits the codec).
- **Diagnostics**: `"Decoder.DeviceLost"` (Error on loss, Info on fallback/restore) + terminal `"Decoder.DeviceLost.RecoveryFailed"` (only if software fallback itself fails 3 times).
- **Added a new public enum value `VideoEngineState.Error`** (additive, fully backwards-compatible — no existing values renumbered). Raised by `RunDecoderDeviceLostRecovery` in both recovery-exhausted paths (no-reopenable-path and software-fallback-exhausted), alongside the existing `Error`-level diagnostic. Lets a host bind UI playback affordances to a non-recoverable engine state without correlating diagnostics.
- **`Close()` stops and deterministically joins (`StopAndWait`) the recovery scheduler** before teardown, so a recovery worker mid-`HardwareDecodeDeviceAvailable` (opening a throwaway VideoDecoder) is not torn down mid-native-call. `Dispose()` disposes the new scheduler.

## Files Changed

**New files:**
- (none — all additions went into existing files)

**Modified files:**
- `FatzBombMedia.MediaControl/Core/Graphics/D3D/Diagnostics/D3D11DeviceLostClassifier.cs` — added `IsDeviceLost(IntPtr)` overload.
- `FatzBombMedia.MediaControl/Core/Video/VideoDecoder.cs` — added consecutive-failure counter, `IsHardwareDecodeRepeatedlyFailing` property, `NoteHardwareDecodeFailure` helper, counter reset in `ProcessVideoFrameFromDecoder` and `Close`.
- `FatzBombMedia.MediaControl/Core/Video/VideoEngineState.cs` — added `Error` enum value (additive).
- `FatzBombMedia.MediaControl/Pipeline/VideoPipeline.cs` — added recovery constants, `_decoderRecoveryScheduler` field, `_currentVideoPath` field, `_decoderRecoveryInProgress` flag, `_hardwareDecodeDisabledByDeviceLoss` flag; capture `_currentVideoPath` on `Open`; added `CheckForHardwareDecodeDeviceLoss`, `TriggerDecoderDeviceLostRecovery`, `RunDecoderDeviceLostRecovery`, `FallBackToSoftwareDecode`, `ReprobeAndRestoreHardware`, `HardwareDecodeDeviceAvailable`, `SwapVideoDecoder`; hook detection into the decode loop; raise `VideoEngineState.Error` on failure; deterministic scheduler teardown in `Close()`; dispose the new scheduler in `Dispose()`.
- `FatzBombMedia.MediaControl.Tests/D3D11DeviceLostClassifierTests.cs` — added 1 new test for the `IntPtr.Zero` path of the new overload.
- `docs/roadmap/STAGE_MANIFEST.md` — remove the 005C line (stage is complete).

## Validation

- `dotnet build FatzBombMedia.sln -c Release` (TreatWarningsAsErrors=true) — 0 warnings, 0 errors.
- `dotnet test` — 138 passed, 0 failed (137 existing + 1 new classifier test), no regressions.
- WPF adapter boundary check: no new Core renderer/D3D11/pipeline/internal-Cdg namespaces pulled into `FatzBombMedia.MediaControl.Wpf`.
- Manual smoke test (normal playback across video/CDG/image/sidecar background/secondary output/resize/fullscreen, PLUS the new scenario: GPU device-loss during hardware-decoded video verifying seamless software fallback + auto-recovery when the device returns) is still outstanding — could not be automated; see `docs/VALIDATION.md`.

## Behavior Notes

- The runtime software fallback itself breaks the 005B feedback loop: once the decoder falls back to software, it stops emitting hardware frames pointing to the lost device, so the presentation side (005B) stops re-adopting the dead device. There is a bounded transient between loss detection and fallback completion where a few lost-device frames may briefly cycle 005B before `_generation` invalidation settles it.
- Audio is deliberately left running during a decoder swap — audio keeps the venue-app show going through the recovery window. For CDG karaoke, the CDG overlay is rendered by Core's CDG pipeline (not the FFmpeg decoder), so it also keeps running.
- Loading a new file restores hardware automatically: `Open(path)` calls `FromConfiguration` which uses the configured `VideoDecoding` (typically `Auto`), so the new decoder re-probes hardware from scratch. The software fallback is scoped to the current decoder instance only.
- The `D3D11DeviceLostClassifier.IsDeviceLost(IntPtr)` overload's AddRef is balanced by the Vortice wrapper's Dispose — no lifetime change to the FFmpeg-owned device.
- `VideoEngineState.Error` is the first non-lifecycle state in the enum. It is only raised when software fallback has also failed (rare). The `Error` diagnostic is still the primary signal; the state lets a host bind UI playback affordances without correlating diagnostics.
- The classifier tests only cover the deterministic seams (`Result`-based and `IntPtr.Zero` path). The actual decode/recovery integration requires a live GPU/driver and remains manual-smoke-only.

## Deferred / Follow-Up Items

- **Manual smoke test** of a simulated device-lost condition (GPU adapter disable/re-enable or driver TDR) during hardware-decoded video playback — the outstanding validation gap for Stages 005A, 005B, and 005C. Requires real hardware, cannot be automated.
- **Pre-existing gap (not introduced by this stage):** the FFmpeg hardware-decode device path is only observed via the success/failure of `avcodec_receive_frame` and `av_hwframe_transfer_data` — a device-lost that manifests only in `avcodec_send_packet` is not counted toward the consecutive-failure threshold. If observed in practice, extend the counter to also note `avcodec_send_packet` hard failures.
- **Re-probe interval (`HardwareDecodeReprobeInterval = 5s`)** is hard-coded; if a host wants a different cadence, this is a constant that would need to move to configuration.
