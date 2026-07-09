<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: COMPLETED.

# Stage 009A — Background/Sidecar Video Frame Reuse

## Status

**Completed.** The per-frame allocation concern was resolved during the pooled-buffer refactor (007A.2). No further implementation needed.

## Why This Existed

The VLC comparison identified background/sidecar video per-frame `new byte[]` copies as one of the worst allocation risks, especially at high resolutions.

## How It Was Resolved

| Concern | Resolution | Location |
|---------|-----------|----------|
| Per-frame `new byte[]` for background pixels | Decoder pool-rented buffer passed as `ReadOnlyMemory<byte>` — no allocation | `PipelineMediaEngine.cs:534` |
| Per-frame re-allocation of coordinator copy buffer | Reusable `_compositionBackgroundCopyBuffer` re-allocated only on resize | `NativeMediaPresentationCoordinator.cs:1209-1213` |
| Per-present snapshot allocation | Dedicated `CompositionBackgroundPool` (ArrayPool) instead of `new byte[]` | `NativeMediaPresentationCoordinator.cs:37-42, 1154-1158` |

Two full-frame memcpy operations remain per frame (pool → master → snapshot), but these are CPU cost, not GC allocation pressure.

## Must Not Break — Verified

- ✅ CDG plus image/video sidecar behavior.
- ✅ Background video loop behavior.
- ✅ Explicit background arrays.
- ✅ Core ownership of final composition.
