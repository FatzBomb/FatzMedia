# Stage Update

Stage: 041A.3 - Post-seek recovery state machine extracted
Date: 2026-10-18

## Status: IMPLEMENTED - pending manual smoke

Owner request: "do 041a". This slice is the third and last 015A seam.

Delivered:

- `Pipeline/PostSeekRecovery.cs` holds the state machine and its constants, taken out of
  `VideoPipeline` unchanged:
  - The recovery window lasts 450 ms, 700 ms with fewer than 3 frames queued, and 900 ms with
    none.
  - Two adaptive-lead windows are skipped after it.
  - The warm-up decode budgets and target are included.
  - Thread-safe: the seek path begins recovery while the presentation loop reads it.
  - Times are passed in as Stopwatch timestamps, so the rules are testable without waiting.
  - `Begin`, `Reset`, `IsActive`, `ShouldSuppressLateFrameDrop`.
  - `NextWindow`: recovering, suppressed (using one window), or adapt.
- `VideoPipeline` owns one instance. The warm-up decode loop stays in `VideoPipeline`, because
  it works on the decoder under the decode lock; it takes its budgets and target from the
  class.
- Tests: `PostSeekRecoveryTests` cover window lengths, the warm-up target, the full
  recovery-then-suppression sequence, a new seek restarting the window, and reset.

The three 015A seams are now done (slices 1-3).

## Verification

- Linux: the full solution builds with no warnings; Portable 953 tests pass.
- A Sonnet review compared every moved expression in slices 1-3 with the original inline code
  and found them behavior-identical: conditions, operators, constants, clamps, NaN and zero
  handling, Interlocked/Volatile use, and the order of reads and side effects. It found no
  leftover references to the removed members. The only differences are cosmetic: decoder
  getters are now read once as arguments instead of short-circuited, and the reset uses
  `Interlocked.Exchange`.

## Manual smoke (Windows, quick; covers slices 1-3)

1. Seek around a video (clicks and drags, including far jumps into long-GOP files): playback
   resumes as before, with no new hitch after the seek.
2. Loop a short video with and without audio: it wraps cleanly over several laps.
3. Play to the end with Loop off: it stops at the end.
4. Optional, with performance logging: after a seek the `[VideoPipeline:Perf]` lines show
   `adaptive=seek-recovery`, then `post-seek-suppress remaining=1` and `remaining=0`, then
   `hold`/`increase`/`decrease`, as before.
