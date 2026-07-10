<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: FUTURE.

# Future Stage 011A — Seamless Primary Video Looping Polish

## Status

Future. Proposed order: 011A.

## Why This Exists

Audio-backed looping is gapless (done in 012A.S3 via `TryPreSeekLoopStart` + `PrepareLoopWrap`). Video-only looping still uses the reactive `TryRestartLoopPlayback` fallback — a full decoder seek that may show a frame jump at the loop boundary. `TryPreSeekLoopStart` explicitly guards `!HasAudio` (VideoPipeline.cs:1767) and never runs for video-only media.

## Not Active Until

Promote only if seamless video-only looping becomes a hard product requirement or a specific repro becomes important. The audio-backed path is the primary use case and is already gapless.

## Intended Direction

Adapt the proactive pre-seek pattern from audio-backed loops for video-only media — seek to 0 while tail frames are still queued, so position-0 frames are ready when the virtual clock wraps.

## Must Not Break

- Audio-backed gapless loop (012A.S3)
- Normal video playback
- Video-with-audio sync
- Video without audio clock behavior
- Existing working sidecar/background loops

## First Safe Step

Capture reproducible loop-boundary logs for video-only media and compare against the audio-backed proactive path to identify the exact gap.
