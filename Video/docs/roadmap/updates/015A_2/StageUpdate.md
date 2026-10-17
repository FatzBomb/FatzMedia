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
