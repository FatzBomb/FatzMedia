<!-- Conditional future stage. Promote only when its activation condition is met. -->

Stage file status: FUTURE.

# Future Stage 018A — CDG Snapshot Ownership Redesign / Pooling


## Status

Future. Proposed order: 018A.

## Why This Exists

CDG snapshots allocate buffer copies. Pooling or dirty-only snapshots could reduce allocation, but current immutable snapshot ownership makes simple pooling unsafe.

## Not Active Until

Promote only after measurement proves CDG snapshots are a meaningful bottleneck and an ownership model is designed first.

## Intended Direction

Consider dirty-only snapshots, double-buffering, or another explicit ownership model before any pooling.

## Must Not Break

- Lyrics correctness.
- Palette correctness.
- Dirty-region uploads.
- Retained-refresh path.
- Secondary output parity.

## First Safe Step

Measure current snapshot allocation rate and map every retained reference to `CdgRenderSnapshot` before proposing implementation.
