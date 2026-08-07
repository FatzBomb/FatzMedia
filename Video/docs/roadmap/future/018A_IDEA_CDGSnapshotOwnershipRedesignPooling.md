# Idea Stage 018A - CDG Snapshot Ownership and Reuse

Status: Good idea.

## Why This Is Worth Doing

CDG snapshots currently favor simple immutable ownership and copy buffers between
producer and presentation paths. A deliberate ownership model could reduce allocations
and copying while making retention rules more explicit.

## Recommended Direction

- Document every producer, queue, renderer, and retained reference to a snapshot.
- Compare dirty-only snapshots, double buffering, reference-counted leases, and pooling.
- Choose an ownership model before changing allocation behavior.
- Keep immutable snapshots as the correctness reference during migration.

## Recommended Immediate Action

Add allocation and copy counters for representative CDG playback, then create a lifetime
diagram showing exactly when each `CdgRenderSnapshot` can be reused or released.

## Must Preserve

- Lyric, palette, and border correctness.
- Dirty-region upload behavior.
- Retained refresh after stop, resize, and surface reattachment.
- Primary/secondary output parity.

## Explicit Non-Goals

- Returning pooled mutable buffers while consumers still reference them.
- Combining ownership changes with CDG rendering redesign.
- Changing packet interpretation or timing.
- Trading bounded allocations for unbounded retained pools.
