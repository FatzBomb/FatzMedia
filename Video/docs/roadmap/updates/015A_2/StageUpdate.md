# Stage Update

Stage: 015A.2 - Seek pump state machine
Date: 2026-10-17

## Status: IMPLEMENTED - manual Player smoke pending (repeat of the 035A seek checks)

Delivered: see slice 2 in the stage file. `VideoSeekPump` and `IVideoSeekPumpHost` are new;
`VideoPipeline` lost about 250 lines of seek-pump state and loop, kept as one-line
delegates in `VideoPipeline.SeekPump.cs`.

Tests: 12 cases in `VideoSeekPumpTests.cs` (Portable), all synchronous except one that runs
the real worker thread.

## Decisions made without the owner

- **No behaviour change.** The loop, the thresholds, and the order of checks were moved as
  they were, including the 035A rules (scrub settles on the shown frame; Play during a seek
  waits for the settle). The tests pin that order.
- `SeekCore` stays in `VideoPipeline`: it touches the decoder, audio engine, clocks, and
  presentation loops. The pump only decides what to seek and when.
- The adaptive interval EMA keeps its own lock instead of sharing the pump's (no invariant
  links them).

## Verification

- Linux: full-solution Release build clean; Portable 750/750.
- Not runnable here: real video seeking. The smoke below repeats the checks the owner
  passed for 035A.

## Manual smoke (Windows, native DLLs)

1. Play a video, drag the seek bar and hold still: playback carries on from the frame on
   screen with no pause or jump; release without moving: no jump.
2. A single click on the seek bar lands at the clicked position.
3. Fast back-and-forth drags keep up (latest position wins).
4. Seek while paused, then press Play: playback starts at the new position, never at the
   old one first.
5. Loop on: a video loops cleanly at its end, and seeking near the end still works.

## Review follow-up (same day, local review instead of a hosted one)

A high-effort review of the slice found no behaviour change in the move. Fixed:

- The `SeekPump` property allocated a delegate on every access (once per presented frame
  and in seek decode loops). It is now a volatile read after a locked one-time creation.
- Pre-existing, moved along: if `Stop` timed out joining a worker whose seek still held
  the transport gate, a later `Start` could revive that worker next to the new one. `Start`
  now waits for a lingering worker while the shutdown flag is still set.
- Pre-existing: preview and settle exceptions went only to `Debug.WriteLine` (dropped in
  Release). They are now `VideoPipeline` warnings.
- One test asserted nothing; it is fixed, and a new test covers a preview without a frame
  while a newer request waits.
- Removed an unused `IsShuttingDown` property and a stale lock note in the stage file.

Not changed: the interval-wait round trip and the one-line delegates (they keep the move
behaviour-identical and the tests deterministic), and the target normalization in `Seek`
(as before). `CdgPipeline` keeps its own copy of the latest-wins worker; folding it into
`VideoSeekPump` is recorded as ENG-013.

