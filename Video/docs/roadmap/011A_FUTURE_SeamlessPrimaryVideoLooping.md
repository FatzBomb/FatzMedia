<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: FUTURE.

# Future Stage 011A — Seamless Primary Video Looping Polish


## Status

Future. Proposed order: 011A.

## Why This Exists

Primary/standalone video looping is functional and preserves the surface, but some files may show a small hold or frame jump at the loop boundary.

## Not Active Until

Promote only if seamless looping becomes a hard product requirement or a specific repro becomes important.

## Intended Direction

Improve loop-boundary behavior without destabilizing normal video playback or sidecar/background loops.

## Must Not Break

- Normal video playback.
- Video-with-audio sync.
- Video without audio clock behavior.
- Existing working sidecar/background loops.

## First Safe Step

Capture reproducible loop-boundary logs and define whether the problem is decoder seek, frame selection, audio restart, or presentation retention.
