# Stage Update

Stage: 041A.1 - Video loop-boundary rules extracted
Date: 2026-10-18

## Status: IMPLEMENTED - pending manual smoke

Owner request: "do 041a" (after the roadmap cleanup collected the completed stages'
follow-ups into 041A). This slice is the first 015A seam.

Delivered:

- `Pipeline/VideoLoopBoundary.cs` (pure, static), taken out of `VideoPipeline` unchanged:
  - `EndToleranceSeconds`: one frame, kept between 20 and 120 ms, 50 ms without a frame rate.
  - `LoopBoundarySeconds`: the shorter of the media and video-stream durations.
  - `LoopOverflowPosition`: where a clock past the boundary lands in the next lap.
  - `HasReachedNaturalEnd`.
  - `ShouldRestartLoop`: drained at the boundary, or a tolerance past it with nothing queued.
  - `HasDrainedAtEnd`: EOF with nothing queued; with audio, only at the boundary.
- `VideoPipeline` calls them. Its private helpers (`GetEndToleranceSeconds`,
  `GetLoopBoundarySeconds`, `GetLoopOverflowPosition`, `HasReachedNaturalEnd`,
  `ShouldRestartLoopPlayback`, `HasReachedPlaybackEnd`) keep their names and call sites.
- Tests: `VideoLoopBoundaryTests`. They cover tolerance bounds, the boundary choice, overflow
  wrapping and invalid input, natural end, restart decisions, and drained end with and
  without audio.

## Decisions made without the owner

- No behavior change: each rule is the original expression, now with a name. The two-step
  restart check became one `||` of the same two conditions.

## Verification

- Linux: the full solution builds with no warnings; Portable 932 tests pass.

## Manual smoke (Windows, quick)

1. Loop a short video with audio (Loop on): it wraps at the end without a freeze or a jump,
   several laps.
2. Loop a video without audio: same.
3. Play a video to the end with Loop off: it stops at the end as before.
