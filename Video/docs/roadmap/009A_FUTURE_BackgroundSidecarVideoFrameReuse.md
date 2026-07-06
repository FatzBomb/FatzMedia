<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: FUTURE.

# Future Stage 009A — Background/Sidecar Video Frame Reuse


## Status

Future. Proposed order: 009A.

## Why This Exists

The VLC comparison identified background/sidecar video per-frame `new byte[]` copies as one of the worst allocation risks, especially at high resolutions.

## Not Active Until

Do not promote until production-safety stages are underway or a measured allocation problem points here.

## Intended Direction

Reduce or eliminate full-frame per-frame allocations in background/sidecar video presentation while preserving sidecar timing and CDG/background layering.

## Must Not Break

- CDG plus image/video sidecar behavior.
- Background video loop behavior.
- Explicit background arrays.
- Core ownership of final composition.

## First Safe Step

Measure allocation rate for sidecar/background video and map buffer ownership from decoder through `PipelineMediaEngine` and `NativeMediaPresentationCoordinator`.
