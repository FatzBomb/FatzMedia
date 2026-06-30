# Stage 11 Video Timing Immediate Fix

## Problem

Primary video playback still develops visible frame loss/stutter during 23.98fps playback even after Stage 11 source-cadence pacing changes.

The remaining issue is not a renderer overload and not a late-drop path. Current logs show the playback loop is still reporting increasing `selectionSkips` while `lateDrops` remains `0`.

This means the video selector is choosing a newer queued frame and returning/skipping the older one, even though the older frame has not missed a full source-frame display window.

## Important Log Readings

Recent run:

```text
[VideoPipeline:Perf] presented=120, dropped=12, selectionSkips=12, lateDrops=0,
maxClockLate=31.2ms, maxTargetLate=43.7ms, maxEarly=10.4ms,
clockLateSamples=0, clockLateThreshold=31.3ms,
lead=12.5ms (0.750x refresh), adaptive=hold-source-cadence,
queued=6/6, fps=23.98, videoFrame=41.7ms, refresh=16.7ms
```

Later in the same run:

```text
[VideoPipeline:Perf] presented=120, dropped=21, selectionSkips=21, lateDrops=0,
maxClockLate=31.4ms, maxTargetLate=43.9ms, maxEarly=10.2ms,
clockLateSamples=1, clockLateThreshold=31.3ms,
lead=12.5ms (0.750x refresh), adaptive=hold-source-cadence,
queued=5/6, fps=23.98, videoFrame=41.7ms, refresh=16.7ms
```

Supporting readings:

```text
[VideoDecoder:Perf] frames=120, avgTotal=1.48ms, scale=1.47ms, queue=6/24, hw=software
[VorticeD3D11:PresentPerf] frames=120, mode=Immediate, avgPresent=0.14ms, maxPresent=0.69ms
[COMPOSITION-D3D11:10X] passes=120, avgPass=1.73ms, maxPass=3.35ms
```

These values indicate:

- Decode is fast enough.
- Present is fast enough.
- Composition is usually fast enough.
- The decoded queue is not starving.
- `lateDrops=0`, so the hard late-drop path is not causing the visible loss.
- `selectionSkips` is the current failure signal.

## Root Interpretation (Original — Superseded)

The original interpretation below assumed the frame selector was advancing too eagerly inside the
source-frame window. Investigation showed this interpretation was incorrect. It is kept here for
history; see "Corrected Root Cause" for the real defect and fix.

> The current selection logic still allows frame advancement based on the lead-adjusted target window.
>
> For 23.98fps:
>
> - Source frame duration is about `41.7ms`.
> - Current logs show skips starting around `31ms` clock late.
> - `31ms` is late, but still inside one source-frame duration.
> - Therefore the selector is skipping frames before they have truly aged out of their intended source-frame display window.
>
> The current code has started to guard against this, but the problem still exists, so the guard is either insufficient or is being evaluated too late in the selection loop.

## Corrected Root Cause (Resolved)

The frame selector was not the defect. The selection latch the immediate-fix target asked for was
already present in `VideoPipeline.PresentFrameForPosition` / `ShouldAdvanceSelectionToFrame`, and is
in fact stricter than the requested rule: it refuses to advance while
`clockPosition - selectedPresentationTime < sourceFrameDuration`, and additionally requires
`nextPresentationTime <= clockPosition`.

The real defect was presentation-loop **pacing precision**:

- The presentation loop paced itself with `cancellationToken.WaitHandle.WaitOne(TimeSpan)`.
- `WaitOne(TimeSpan)` is bound by the Windows process timer resolution, which defaults to roughly
  `15.6ms`.
- A `41.7ms` (23.98fps) wait therefore rounds up to about `46.8ms` (3 x 15.6ms).
- The audio-mastered presentation clock (`AudioPresentationClock`) advances at true wall-clock rate,
  so the presentation point drifted behind the clock by about `5ms` per tick.
- When the accumulated drift crossed one full source-frame (`41.7ms`), the selector correctly
  performed a single catch-up advance, dropping one frame. That advance increments
  `selectionSkips` while `lateDrops` stays `0`.

This explains every symptom in the logs:

- `selectionSkips` climbs steadily during otherwise healthy playback.
- `lateDrops=0` (the 180ms hard-drop path is never hit).
- `maxClockLate~31ms` is the pre-skip peak lateness recorded for the *presented* frame; the *dropped*
  frame was a full ~`41.7ms` behind at the instant of the skip, which the diagnostic never records
  because the skip resets lateness.
- The decoded queue stays full (`5/6`, `6/6`) — throughput was never the problem, only pacing.

## Resolution

Added `FatzBombMedia.MediaControl/Core/Threading/PrecisionWaiter.cs`, a high-resolution cancellable
waiter backed by a Windows high-resolution waitable timer
(`CreateWaitableTimerExW` + `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`, Windows 10 1803+). It provides
~1ms wait precision without raising the process-global timer resolution (no `timeBeginPeriod`). It
falls back transparently to `WaitHandle.WaitOne(TimeSpan)` if the high-resolution timer cannot be
created.

The waiter replaced the coarse `WaitOne(TimeSpan)` pacing in the fixed-cadence loops:

- `VideoPipeline.PresentationLoop` (primary video; later changed again in Stage 11-22 to phase-lock
  source-cadence waits to the audio-mastered clock).
- `NativeMediaPresentationCoordinator.RenderLoop` (DxCdg/CDG overlay cadence).
- `PipelineMediaEngine.BackgroundVideoLoop` (sidecar/background video).

The first pass preserved the existing `nextTick` accumulator and only changed the wait primitive.
Runtime logs then proved that timer precision was fixed, but the fixed-interval accumulator was still
free-running against the audio-mastered clock. Stage 11-22 therefore changed primary source-cadence
video waits to be clock-locked: after each present, the loop peeks the next queued frame and waits
until that frame enters the selector window relative to the live presentation clock. No frame-selection
logic was modified — `ShouldAdvanceSelectionToFrame` and `VideoPresentationTiming` are unchanged.

## Steps Already Taken

1. Changed primary video presentation from display-refresh polling to source-cadence pacing when FPS is known.

2. Fixed adaptive lead feedback so it no longer uses lead-adjusted `maxTargetLate` as the trigger.

3. Added diagnostics:

```text
clockLateSamples
clockLateThreshold
selectionSkips
lateDrops
```

4. Made adaptive lateness threshold source-frame-aware.

5. Held adaptive lead steady for source-cadence 24/30fps playback:

```text
adaptive=hold-source-cadence
```

6. Added a conservative primary frame selection lead limit.

7. Added an attempted source-cadence guard so the selector should not advance while the current frame is still inside its source-frame display duration.

## What The Latest Logs Proved

The adaptive lead fixes are working:

```text
lead=12.5ms (0.750x refresh)
adaptive=hold-source-cadence
lateDrops=0
```

The high-resolution timer fix is also working:

```text
[VideoPipeline:Loop] avgTickGap=41.71ms, maxTickGap=42.5ms, target=41.7ms,
sourceFrame=41.7ms, displayRefresh=16.7ms, timer=high-res
```

The remaining problem was not timer resolution and not premature selector advancement. It was
phase drift between a fixed source-cadence video loop and the audio-mastered clock:

```text
maxClockLate=30.1ms -> 32.7ms -> 37.8ms -> 41.7ms
selectionSkips=0 -> 1 -> 3 -> 7 -> 25
maxDroppedClockLate=42.8ms -> 47.4ms -> 55.0ms -> 64.8ms
```

Those values prove the selector only skipped frames after the dropped frame was at least one full
source frame behind the real clock. The skipped frames were legitimate catch-ups; the defect was that
the fixed `nextTick += sourceFrameDuration` loop accumulated small audio-clock/frame-rate mismatch
until catch-up skips became necessary.

## Immediate Fix Target (Original — Already Implemented)

The original target asked for a retained frame latch for source-cadence playback:

```text
For source-cadence playback:
  If selectedFrame exists:
    keep selectedFrame unless realClockPosition >= selectedFrame.PresentationTime + sourceFrameDuration
  Only then allow advancing to nextFrame.
```

This rule was already implemented in `ShouldAdvanceSelectionToFrame` (and is stricter than the rule
above). Re-implementing it does not change behavior. The actual fix was loop pacing precision; see
"Resolution".

## Files Involved

- `FatzBombMedia.MediaControl/Core/Threading/PrecisionWaiter.cs` (new — high-resolution cancellable waiter)
- `FatzBombMedia.MediaControl/Pipeline/VideoPipeline.cs` (presentation loop uses the precise waiter and phase-locks source-cadence waits to the live clock)
- `FatzBombMedia.MediaControl/Core/Graphics/NativeMediaPresentationCoordinator.cs` (DxCdg render loop)
- `FatzBombMedia.MediaControl/Pipeline/PipelineMediaEngine.cs` (background video loop)
- `STAGE11_PRESENTATION_PLAN.md`

Unchanged (confirmed not the defect):

- `FatzBombMedia.MediaControl/Core/Video/VideoPresentationTiming.cs`
- `VideoPipeline.ShouldAdvanceSelectionToFrame`

## Validation Needed After Next Fix

Expected log shape:

```text
adaptive=hold-source-cadence
lead=12.5ms (0.750x refresh)
lateDrops=0
selectionSkips=0
queued=5/6 or 6/6
fps=23.98
videoFrame=41.7ms
```

Acceptable exceptions:

- A small `selectionSkips` spike immediately after seek.
- A small spike after resize/fullscreen if a real loop gap appears.

Not acceptable:

- `selectionSkips` steadily increasing during normal playback.
- `selectionSkips` increasing while `lateDrops=0` and the queue is full.
- Adaptive lead increasing during 23.98fps source-cadence playback.
