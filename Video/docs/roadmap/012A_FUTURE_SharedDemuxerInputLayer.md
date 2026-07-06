<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: FUTURE.

# Future Stage 012A — Shared Demuxer / Input Layer


## Status

Future. Proposed order: 012A.

## Why This Exists

Current video playback demuxes the same container twice: once for video and once for audio. This is acceptable for local karaoke playback now, but it becomes structural debt for subtitles, multi-track audio, chapters, streaming/network input, and coordinated seeks.

## Not Active Until

Do not promote until error reporting, diagnostics, device handling, and state ownership are stable.

## Intended Direction

Move toward a single input/demux layer that reads packets once and fans them out to video, audio, subtitles/CDG, and metadata consumers.

## Must Not Break

- BASS tempo/pitch path.
- Video-with-audio sync.
- Seek preview/settle behavior.
- CDG/archive/sidecar workflows.
- Hardware decoder fallback.

## First Safe Step

Mapping only: document `VideoDecoder`, `VideoAudioEngine`, open/seek, packet ownership, audio discard/prebuffer, and stream lifetime before source edits.
