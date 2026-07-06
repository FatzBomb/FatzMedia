<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: FUTURE.

# Future Stage 016A — ClockAudioEngine / Clock Source Abstraction Split


## Status

Future. Proposed order: 016A.

## Why This Exists

`ClockAudioEngine` is intentionally used for standalone CDG and video-without-audio, but future clock-source abstractions may clarify audio-backed vs clock-backed transport behavior.

## Not Active Until

Do not promote until audio latency feedback and state ownership are stable.

## Intended Direction

Separate clock-source responsibilities cleanly without breaking current no-audio tempo behavior.

## Must Not Break

- Standalone CDG tempo.
- Video without audio tempo.
- Real BASS tempo for audio-backed media.
- Position/duration reporting.

## First Safe Step

Map current `ClockAudioEngine`, `PlaybackClock`, `MediaEffects`, and pipeline usage, then identify naming/ownership confusion only.
