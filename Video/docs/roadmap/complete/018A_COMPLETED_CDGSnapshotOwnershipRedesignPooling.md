# Completed Stage 018A - CDG Snapshot Ownership and Reuse

Prepared: 2026-10-16
Status: Completed 2026-10-18. Slice 1 (lifetime map, counters, immutable snapshot reuse); owner
CDG smoke passed 2026-10-17 (`updates/018A_1`). Reference-counted leases were deliberately not
done (see the ownership table).

## Lifetime Map (pinned)

```
CDG packets (CdgEngine render thread)
  -> CdgStateMachine (indices[64,800 B], palette[16], wash, bg index, dirty list)  _lock
       CreateSnapshot(consumeDirtyRegions)  <- CdgEngine.TryCreateCdgSnapshot (_renderLock)
         = CdgRenderSnapshot (immutable: own index + palette arrays, dirty-rect array)
  -> MediaPresentationCoordinator
       _lastCdgSnapshot (cache, _cdgSnapshotGate)  -- read by retained refresh and by the
                                                     video/hardware composition thread
       MediaCompositionFrame.CdgOverlay / MediaCompositionLayer.CdgSnapshot (per present)
  -> renderer PresentComposition (synchronous)
       Direct3D11: uploads indices (dirty regions or full frame) + palette into textures;
                   layer cache keeps descriptor.WithoutTransientPayload() (snapshot = null)
       Software:   reads indices/palette while composing (DrawCdg)
       Vulkan:     CDG not supported in slice 1 (overlays rejected)
  release: when the coordinator replaces/clears _lastCdgSnapshot (CDG unload, reset) and
           the last in-flight composition frame returns -> garbage collector
```

A snapshot is therefore referenced by (a) the coordinator cache, for an unbounded time
while it is the latest, and (b) any composition frame being presented, possibly on the
video thread while the CDG path already created a newer one. Renderers never retain it
past `PresentComposition`.

## Ownership Model Decision

| Option | Verdict |
|---|---|
| Pooled mutable buffers | Rejected: the coordinator cache plus cross-thread video composition make "nobody references it" hard to prove; explicit non-goal. |
| Reference-counted leases | Deferred: would need leases through the coordinator cache, composition frames, and every renderer; cost is ~65 KB per *changed* frame, which does not justify it. |
| Double buffering | Rejected: two consumers (cache + in-flight video composition) can need two older frames. |
| **Immutable snapshots + reuse when unchanged** | **Chosen.** Snapshots stay immutable (correctness reference). `CreateSnapshot` returns the previous instance when revisions, wash, background index, palette, and pending dirty list are unchanged, and shares the previous immutable buffers when only the previous dirty list is stale. The palette array is shared until the palette changes. |

## Counters

`CdgStateMachine.SnapshotStatistics` (internal): snapshots created, reused, index copies,
palette copies, index bytes copied. Representative stream test
(`CdgSnapshotOwnershipTests.RepresentativeStream_ReusesIdleTicksAndMatchesState`: 300
packets/s, 60 Hz snapshot requests, lyric bursts 1.5 s of every 4 s, 10 s):

| | Before | After |
|---|---|---|
| Snapshot requests | 600 | 600 |
| Index frame copies | 600 (38.9 MB) | 270 (17.5 MB) |
| Palette copies | 600 | 1 |
| Snapshot objects | 600 | <= 273 |

Real savings depend on how often the coordinator asks without a change (retained
refreshes, video-composition reads with pending CDG work); a lyric-dense song with no idle
time sees none.

## Acceptance Criteria (slice 1)

- Lifetime map and ownership decision recorded (above).
- Counters exist and are exercised by a representative-stream test.
- Every returned snapshot equals the state machine at request time (indices, revision).
- Old snapshots are never mutated by later packets.
- Dirty-region semantics unchanged: consuming clears, non-consuming never reuses while
  regions are pending, reset/background-override changes produce new snapshots.

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
