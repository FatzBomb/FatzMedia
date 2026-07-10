<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: FUTURE.

# Future Stage 016A — ClockAudioEngine / Clock Source Abstraction Split

## Status

Future. Proposed order: 016A. Preconditions met (audio latency feedback + state ownership are stable).

## Why This Exists

`ClockAudioEngine` is intentionally used for standalone CDG and video-without-audio, but future clock-source abstractions may clarify audio-backed vs clock-backed transport behavior. The preconditions were audio latency feedback (006A, done) and state ownership (003B-003E, done) — both are now stable.

## Gate cleared (was "Not Active Until")

- Audio latency feedback (006A) — done 2026-07-07
- State ownership (003B-003E) — done 2026-07-05

## Intended Direction

Separate clock-source responsibilities cleanly without breaking current no-audio tempo behavior.

## Must Not Break

- Standalone CDG tempo
- Video without audio tempo
- Real BASS tempo for audio-backed media
- Position/duration reporting

## First Safe Step

Map current `ClockAudioEngine`, `PlaybackClock`, `MediaEffects`, and pipeline usage, then identify naming/ownership confusion only.
