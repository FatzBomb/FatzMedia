# Active Stage 015A - VideoPipeline Responsibility Split

Prepared: 2026-10-16
Status: Slice 1 (responsibility map + seek policy and seek-pump timing extraction) and
slice 2 (seek pump state machine) implemented. Video seeking passed the owner's smoke on
2026-10-17 (035A); slice 2 is a behaviour-preserving move, pending a repeat of that smoke
(`updates/015A_2`).

## Responsibility Map (pinned)

| Responsibility | Members (VideoPipeline) | State / sync | Threads |
|---|---|---|---|
| Open/close/lifecycle | `Open`, `OpenBytes`, `Close`, `Dispose` | decoder, demuxer, generation | caller |
| Transport | `Play`, `Pause`, `Stop`, loop restart (`TryRestartLoopPlayback`, `CommitAudioLoopWrap`, `TryCommitVideoOnlyLoopWrap`) | `_clock`, audio engine | caller, presentation loop |
| Decode loop | `DecodeLoop`, `DecodeVideoBatch`, `DecodeUntilFrameAvailable` | frame queue | decode scheduler |
| Presentation loop | `PresentationLoop`, `PresentFrameForPosition`, adaptive lead | current frame, diagnostics | presentation scheduler |
| Seek pump | `SeekCore` (stays); **`VideoSeekPump`: worker loop, pending/active/sequence/committed targets** (slice 2); **adaptive pump interval** (slice 1) | pump's own lock and signal | seek worker |
| Seek frame policy | **normalization, superseding threshold, frame acceptance, decode budgets** | none (pure) | seek worker |
| Post-seek recovery | warmup decode, late-drop suppression | counters | seek worker, presentation |
| Device loss / HW fallback | `TriggerDecoderDeviceLostRecovery`, `SwapVideoDecoder`, `FallBackToSoftwareDecode` | decoder swap | recovery task |

Bold rows/items are extracted (slice 1 unless marked).

## Slice 1 (implemented)

- `VideoSeekPolicy` (pure statics + constants): `NormalizeTarget`,
  `IsMeaningfullyDifferentTarget`, `IsFrameAcceptable`, `GetDecodeBudgetMilliseconds`,
  `GetDecodeMaxAttempts`.
- `AdaptiveSeekPumpTiming` (thread-safe EMA): `Record`, `IntervalMilliseconds`, `Reset`.
- `VideoPipeline` keeps its private methods as one-line delegates, so every call site,
  value, and lock scope that mattered is unchanged (the EMA had no cross-field invariants
  with other `_seekPumpLock` state; `ResetAdaptiveVideoSeekTiming` still resets it and the
  committed-target floor together under `_seekPumpLock`).

## Slice 2 (implemented)

Added 2026-10-17 after the 035A smoke rounds: every seek bug found in smoke lived in the
seek pump, and its ordering could only be checked by hand.

- `VideoSeekPump` (`Pipeline/VideoSeekPump.cs`) owns the pending, active, sequence, and
  minimum-committed targets, the `VideoSeek` worker thread, and the preview-then-settle
  loop (lone click exact; active scrub previews nearby frames and settles on the shown
  frame; superseded settles skipped). The loop body is `DrainPendingRequests`, which tests
  run synchronously.
- `IVideoSeekPumpHost` is what the pump needs from the pipeline: target normalization,
  `RunSeek` (the existing `SeekCore`), the current frame time, and the pump-interval wait.
- `VideoPipeline` implements the host in `VideoPipeline.SeekPump.cs` and keeps its private
  helpers as one-line delegates, so `SeekCore`, `Play`'s deferred-start rule, the loop
  restart, `Stop`, and post-seek recovery call sites are unchanged.
- The pump's state used to share `_seekPumpLock` with the adaptive timing EMA. The EMA is
  thread-safe on its own and has no invariant with the targets (as slice 1 noted), so
  `ResetAdaptiveVideoSeekTiming` now resets the two separately.
- 12 focused tests (`VideoSeekPumpTests`): click, drag-hold, release repeat, latest-wins
  bursts, preview without a frame, normalization, the seek sequence across a settle, the
  superseding rule, active-target clearing, cancel/reset, and the real worker thread.

## Next Seams (candidates, in order)

1. Loop boundary math (`GetLoopOverflowPosition`, end tolerance, natural-end detection).
2. Presentation-lead controller (`UpdateAdaptivePresentationLead` and thresholds).
3. Post-seek recovery state machine.

## Explicit Non-Goals

As in the original idea: no line-count splitting, no timing/queue/seek/drop policy change.
