<!-- Conditional future stage. Promote only when its activation condition is met. -->

Stage file status: FUTURE.

# Future Stage 014A — Streaming / Network Playback


## Status

Future. Proposed order: 014A.

## Why This Exists

Streaming/network input is wanted eventually, but it changes assumptions around I/O stalls, buffering, seeking, error reporting, and reconnect behavior.

## Not Active Until

Do not promote until error reporting, diagnostics, and input/demux architecture are better prepared.

## Intended Direction

Introduce network/stream input through an input-source abstraction without weakening local karaoke file performance.

## Must Not Break

- Local file playback speed and stability.
- Archive/ZIP CDG workflows.
- Sidecar detection.
- Seek behavior for seekable files.

## First Safe Step

Define supported stream types and map which current file assumptions would fail on non-seekable or slow inputs.
