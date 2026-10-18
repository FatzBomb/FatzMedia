# Stage Update

Stage: 041A.2 - Adaptive presentation-lead rules extracted
Date: 2026-10-18

## Status: IMPLEMENTED - pending manual smoke

Owner request: "do 041a". This slice is the second 015A seam.

Delivered:

- `Pipeline/PresentationLeadController.cs` (pure, static) is taken out of `VideoPipeline`
  unchanged. It holds the controller's constants (default 0.75 refreshes, range 0.50 to 1.25,
  steps +0.125 and -0.0625, thresholds) and its rules:
  - `InitialLeadRefreshes`: the configured lead, or the default, clamped to the range.
  - `LeadSeconds`: the lead in refreshes (60 Hz when unknown), at most 45 % of a frame and
    at most 30 ms.
  - `ClockLateThresholdMilliseconds`: the larger of 0.65 refreshes and 0.75 frames.
  - `Adjust`: a step up on late presents (8 late samples, or one past twice the threshold);
    a half step down when nothing was late and presents were consistently early; otherwise
    hold.
- `VideoPipeline` keeps its state (the active lead and the per-window diagnostics) and its
  diagnostic text. It calls the controller for the numbers.
- Tests: `PresentationLeadControllerTests` cover the initial lead, lead capping, the late
  threshold, increase, decrease, and hold.

## Verification

- Linux: the full solution builds with no warnings; Portable 942 tests pass.

## Manual smoke (Windows, quick)

1. Play a 24 fps video for a minute with display-locked off: as smooth as before.
2. With performance logging on, the `[VideoPipeline:Perf]` lines still show `lead=...` and
   `adaptive=hold|increase|decrease`, as before.
