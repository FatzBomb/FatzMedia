# Stage Update

Stage: 031B.1 - Fade envelope math
Date: 2026-10-16

## Status: IMPLEMENTED - pure logic, no manual smoke required for this slice

Delivered:

- Public `AudioFadePoint`, `AudioFadeEnvelope`, `AudioCrossfadeOptions`.
- Internal `Core/Audio/Fade/AudioFadeSchedule` (envelope -> chained linear ramps,
  plateau skipping, multiplicative gain composition) and `AudioFadeRunner`
  (latest-wins, cancellable chained ramps over an `IAudioFadeGainTarget`).
- 43 focused cases in `FatzBomb.MediaControl.Portable.Tests/AudioFadeTests.cs`.
- No production playback path consumes the new types in this slice.

## Decisions made without the owner

- Out-of-range points throw instead of clamping (the spec calls points authoritative).
- Fade duration clamps to 60 s; negative/zero disables.
- An envelope that never becomes audible reports `AudibleStart = 1`.

## Verification

- Linux: Release build 0 warnings/errors; 43/43 fade cases pass.
