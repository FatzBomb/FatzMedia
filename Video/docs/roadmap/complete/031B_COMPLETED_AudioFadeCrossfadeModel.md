# Completed Stage 031B - Audio Fade and Crossfade Model

Prepared: 2026-10-16
Status: Completed 2026-10-18. Slice 1 (envelope math) and slice 2 (single-handle fades,
fade-through prepared-next); owner smoke passed 2026-10-17 (`updates/031B_1`,
`updates/031B_2`). The deferred overlapping crossfade moved to
`future/040A_IDEA_OverlappingCrossfade.md`.

## Objective

A deliberate, testable fade envelope model for ordinary local audio, applied without
fighting user volume or normalization, and hooked into the Stage 031A prepared-next
transition.

## Pinned Decisions

1. **Points.** `AudioFadePoint(position 0..1, gain 0..1)`; out-of-range or non-finite
   values throw (points are authoritative, never silently coerced). `-0` normalizes
   to `0`.
2. **Envelopes.** `AudioFadeEnvelope` is immutable and sorted. Defaults: outgoing
   `[(0,1),(1,0)]`, incoming `[(0,0),(1,1)]`. `WithPoint(s)` upserts by exact position
   (last write wins, including over the implicit defaults); no epsilon merging.
3. **Evaluation.** Linear interpolation; positions clamp to `0..1`; gain before the
   first / after the last point holds that point's gain.
4. **Zero plateau.** `AudibleStart` is where a leading all-zero run ends (`0` when the
   envelope starts audible, `1` when it never becomes audible). Incoming playback starts
   there; the schedule skips the plateau's wall-clock time so muted lead time does not
   consume source audio.
5. **Options.** `AudioCrossfadeOptions` - shared `Duration` (negative -> 0 = disabled,
   clamped to 60 s), `Outgoing`, `Incoming` (null -> default). Disabled by default.
6. **Gain composition.** Final gain = envelope x user volume x normalization. Each is an
   independent native stage: user volume is the BASS channel volume attribute,
   normalization is the existing gain FX, and the envelope is a dedicated
   `BASS_FX_VOLUME` stage. `AudioFadeSchedule.ComposeGain` pins the math.
7. **Native ramps.** Each envelope segment becomes one native linear ramp
   (`BASS_FX_VOLUME` `fTime`), chained by `AudioFadeRunner`. No BASSmix dependency.
8. **Latest wins.** Starting a fade cancels the running one, which leaves the gain
   where its last ramp reached. Open, Stop, and Close reset the envelope gain to 1.
9. **Fades are wall-clock.** Tempo does not stretch fades; loop wraps during a fade do
   not restart or interrupt it (the envelope stage is independent of position).

## Slices

- **Slice 1** - public envelope/options types, schedule, runner, focused tests
  (`updates/031B_1`).
- **Slice 2** - envelope FX stage in `AudioEngine`, public fade API, fade-through
  prepared-next transitions, Player flags (`updates/031B_2`).

## Deferred

- **True overlapping crossfade** (outgoing and incoming audible at the same time on
  two concurrent BASS handles with takeover at the end). The current engine owns one
  stream set per player and the clock, position, end, and loop logic are tied to it.
  Overlap needs a second stream owner inside `AudioEngine` (or BASSmix) plus native
  validation of takeover, which cannot be validated in the unattended Linux session.
  The envelope/schedule/runner from slice 1 are reusable unchanged for it.

## Explicit Non-Goals

- Video dissolve or fade-through-black.
- Forcing a BASSmix dependency.
- Live/streaming crossfades (Stage 014A).
- Multi-track mixing.
