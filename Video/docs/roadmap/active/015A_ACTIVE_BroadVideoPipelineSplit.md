# Active Stage 015A - VideoPipeline Responsibility Split

Prepared: 2026-10-16
Status: Slice 1 (responsibility map + seek policy and seek-pump timing extraction)
implemented, pending manual video smoke.

## Responsibility Map (pinned)

| Responsibility | Members (VideoPipeline) | State / sync | Threads |
|---|---|---|---|
| Open/close/lifecycle | `Open`, `OpenBytes`, `Close`, `Dispose` | decoder, demuxer, generation | caller |
| Transport | `Play`, `Pause`, `Stop`, loop restart (`TryRestartLoopPlayback`, `CommitAudioLoopWrap`, `TryCommitVideoOnlyLoopWrap`) | `_clock`, audio engine | caller, presentation loop |
| Decode loop | `DecodeLoop`, `DecodeVideoBatch`, `DecodeUntilFrameAvailable` | frame queue | decode scheduler |
| Presentation loop | `PresentationLoop`, `PresentFrameForPosition`, adaptive lead | current frame, diagnostics | presentation scheduler |
| Seek pump | `SeekCore`, `SeekWorkerLoop`, pending/active targets, **adaptive pump interval** | `_seekPumpLock`, `_seekSignal` | seek worker |
| Seek frame policy | **normalization, superseding threshold, frame acceptance, decode budgets** | none (pure) | seek worker |
| Post-seek recovery | warmup decode, late-drop suppression | counters | seek worker, presentation |
| Device loss / HW fallback | `TriggerDecoderDeviceLostRecovery`, `SwapVideoDecoder`, `FallBackToSoftwareDecode` | decoder swap | recovery task |

Bold rows/items are extracted in slice 1.

## Slice 1 (implemented)

- `VideoSeekPolicy` (pure statics + constants): `NormalizeTarget`,
  `IsMeaningfullyDifferentTarget`, `IsFrameAcceptable`, `GetDecodeBudgetMilliseconds`,
  `GetDecodeMaxAttempts`.
- `AdaptiveSeekPumpTiming` (thread-safe EMA): `Record`, `IntervalMilliseconds`, `Reset`.
- `VideoPipeline` keeps its private methods as one-line delegates, so every call site,
  value, and lock scope that mattered is unchanged (the EMA had no cross-field invariants
  with other `_seekPumpLock` state; `ResetAdaptiveVideoSeekTiming` still resets it and the
  committed-target floor together under `_seekPumpLock`).

## Next Seams (candidates, in order)

1. Seek pump state machine (added 2026-10-17 after the 035A smoke rounds): pending,
   active, and sequence targets, preview-versus-settle choice, scrub settle target, and the
   Play-during-seek rule, as a class driven through a small interface so its ordering can be
   unit tested without FFmpeg. Every seek bug found in smoke lived here and could only be
   checked by hand.
2. Loop boundary math (`GetLoopOverflowPosition`, end tolerance, natural-end detection).
3. Presentation-lead controller (`UpdateAdaptivePresentationLead` and thresholds).
4. Post-seek recovery state machine.

## Explicit Non-Goals

As in the original idea: no line-count splitting, no timing/queue/seek/drop policy change.
