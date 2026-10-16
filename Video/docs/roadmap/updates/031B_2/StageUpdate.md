# Stage Update

Stage: 031B.2 - Fade application and fade-through prepared-next transitions
Date: 2026-10-16

## Status: IMPLEMENTED - manual Player smoke pending

Delivered:

- `AudioEngine` implements `IAudioFadeGainTarget` with a dedicated `BASS_FX_VOLUME`
  envelope stage added after the normalization gain FX on every local/archive audio
  open. Ramps use `fCurrent = -1`, `fTime = segment seconds`, linear curve.
- Fade sessions: Open, Close, and Stop end the session (pending ramps are rejected)
  and Stop restores unity fade gain, so an interrupted fade never leaves the next
  playback muted.
- Public API: `FadeOutAsync`, `FadeInAsync`, `CancelFade`,
  `PreparedNextTransitionFade` (default `AudioCrossfadeOptions.Disabled`).
- Prepared-next transitions with fade enabled: manual = outgoing fade -> muted lead
  gap (incoming zero plateau) -> load without autoplay -> prime incoming gain -> Play ->
  incoming fade from its audible start. Automatic (natural end) = incoming part only.
  All waiting happens off the control queue; only the load/play step is queued, and
  the automatic path re-validates host intervention before that step.
- Player host: `--fade-next=<seconds>` (with `--prepare-next=` / `--auto-next`).
- 10 new cases (`PlayerFadeTests` + session-ended runner case); 031A/031B total 90.

## Event ordering (pinned)

Manual faded transition: (outgoing fade - no events) -> `PreparedNextChanged`
Ready->Transitioning -> `MediaClosed` -> `MediaLoaded` -> `MediaStarted` /
`StateChanged` Playing -> `PreparedNextChanged` Transitioning->Empty ->
`PreparedNextTransitioned`. Fade completion raises no event; `FadeOutAsync` /
`FadeInAsync` complete their tasks instead. Natural end during an outgoing fade
follows the normal end path; the manual transition then still proceeds if the slot
is Ready (or returns `false` if the automatic transition already consumed it).

## Loop precedence (pinned)

Fades are wall-clock and independent of position: a loop wrap during a fade neither
restarts nor cancels it. `LoopMedia` still suppresses automatic transitions (031A).

## Decisions made without the owner

- Overlapping two-handle crossfade deferred (see the stage file); "crossfade" in this
  slice is fade-through.
- Faded transitions start the incoming item themselves and ignore `PlayOnLoad`
  timing so the fade gain is primed before the first samples.
- Fades apply to every media whose active audio engine is the BASS `AudioEngine`
  (plain audio, audio+CDG, archives); video and clock-only media return `false`.

## Verification

- Linux: full solution Release build 0 warnings/errors; `dotnet format` clean;
  portable 538/539 (known Linux sidecar case); 90 031A/031B cases x 6 runs green.

## Manual smoke (Windows, native DLLs)

1. `--prepare-next="<b.mp3>" --fade-next=4 <a.mp3>`; trigger a manual transition is not
   exposed in the host, so use `--auto-next` for step 2 and validate `FadeOutAsync`
   via step 3.
2. `--prepare-next="<b.mp3>" --auto-next --fade-next=4 <a.mp3>`: at the end of a, b
   starts silent and rises to full over ~4 s; no click at start; volume slider and
   Normalize toggle still behave the same during and after the fade.
3. Normalization multiplicative check: with Normalize on, compare b's final level after
   the fade with the level when loaded normally (should match).
4. Stop during the fade-in, then Play: b plays at full gain (session reset).
5. Pitch/tempo changes during the fade do not alter its wall-clock length.
6. Video next item with `--fade-next`: transition happens, no fade, no errors.

## Review follow-up (same day)

A whole-branch review found three fade-transition defects, fixed with regression tests that
fail on the previous code:

- A faded transition that failed after the outgoing fade (slot cleared or stale, load
  failed) left the current media playing at fade gain 0. The outgoing gain is now restored
  over 250 ms unless another fade owns it.
- A manual faded transition ignored Stop/CancelFade during the outgoing fade and still
  loaded the next item. An interrupted outgoing fade now abandons the transition, and a
  pause, seek, stop, or load during the fade or muted lead abandons it too.
- `CancelFade` racing a fade's completion could throw `ObjectDisposedException`.

The Player host has no manual-transition control, so these paths are covered by the
automated tests only (`PlayerFadeTests.ManualFadedTransition_*`, `AudioFadeRunnerTests`).

## Review follow-up 2 (same day)

- A natural end restores full fade gain, so a song faded out to its end replays or loops
  audibly.
- A fade that interrupts a running fade continues from the gain already reached instead of
  jumping to its own start (no audible step).
- A manual faded transition is pinned to the item that was Ready when it started; preparing
  another item during the fade abandons the transition instead of loading the new one.
- The natural-end gain reset lives in the BASS end callback, which cannot run in unit tests;
  the Player host has no fade-out control, so it is verified by code review only until a
  host exposes `FadeOutAsync`.
