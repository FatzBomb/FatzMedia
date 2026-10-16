# Stage Update

Stage: 018A.1 - CDG snapshot lifetime map, counters, immutable reuse
Date: 2026-10-16

## Status: IMPLEMENTED - pending manual smoke

Delivered: see the stage file for the lifetime map and ownership decision.
`CdgStateMachine.CreateSnapshot` reuses the previous immutable snapshot when nothing
observable changed, shares its buffers when only its dirty list is stale, and shares the
palette array until the palette changes. New internal `CdgSnapshotStatistics` counters.
7 new tests (`CdgSnapshotOwnershipTests.cs`); the existing CDG state-machine tests pass
unchanged.

## Decisions made without the owner

- Chose immutable reuse over pooling/leases (see the decision table): no consumer can see
  a buffer change, so no renderer or coordinator change was needed.
- Reuse compares palette contents as well as `PaletteRevision` (16 ints, defensive):
  `CdgColorManager.CurrentBackgroundArgb` can refresh the LUT outside the state machine's
  revision bookkeeping (no current caller).

## Observations (not changed)

- Direct3D11 uploads only a snapshot's dirty regions when its revision differs from the
  layer's uploaded revision. A layer state that missed an intermediate snapshot (for
  example a secondary output attached between two dirty snapshots) could upload an
  incomplete delta. This predates 018A; clean follow-up snapshots (empty dirty list)
  still force a full upload.

## Manual smoke (Windows, native DLLs)

1. Play a CDG karaoke track (MP3+G or ZIP) on the default renderer and `--software`:
   lyrics draw, highlight, and clear correctly; palette changes (colour fades) look right;
   the border colour is correct.
2. Pause during a lyric line, resize the window, then resume: the frame stays correct.
3. Stop, then resize: retained frame still correct; play again from the start.
4. Open the secondary output while a CDG is playing and compare with the primary.
5. CDG over a video background (sidecar): lyrics composite over video without flicker.
