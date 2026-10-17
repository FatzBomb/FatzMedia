# Stage Update

Stage: 035A.1 - Control-owned seek scrubbing
Date: 2026-10-17

## Status: IMPLEMENTED - manual Player smoke pending

Delivered: see the stage file.

Tests: 7 cases in `SeekScrubTests.cs` and 2 facade cases in `PreparedNextTests.cs`
(`Scrub_ReleaseSeekAtTheDragPosition_CompletesWithoutMoving`,
`Scrub_LoadingMidDrag_EndsTheScrub`), all Portable.

## Revision after the second smoke (same day)

The first version paused playback for the whole drag and resumed on release. The owner
reported that holding the button now paused on the frame ("it used to keep playing, which
is more common"), and release still jumped a second or two. Now:

- Playback carries on during a drag, as before 035A.
- The release seek is ignored when it repeats the last drag position, so nothing restarts.
  This was the jump in both versions: release sought again to the position where the drag
  stopped, either restarting from it after playback had moved on (before 035A) or
  re-seeking a paused video (first version).

## Decisions made without the owner

- **Matching rule:** "same position" means within 1 ms of the scrub's previous seek. The
  Player sends the exact same value on release; a host that rounds differently still seeks,
  so a release is never silently lost.
- Only seeks during a scrub are compared. Outside a scrub, seeking twice to the same
  position still seeks twice (for example "restart from the bookmark").
- New API instead of guessing: without a mouse-down and mouse-up signal the control cannot
  tell a drag's release seek from a deliberate seek to the same position.
- The `IMediaControlSession` members have default implementations, as in 034A, so other
  implementations keep compiling.
- Seek then Play on a video is race-free in general: Play during a pending seek is applied
  when the seek settles. A Play issued right after a seek to the very end no longer
  restarts from the beginning; the seek target wins.

## Verification

- Linux: full-solution Release build clean; Portable tests pass.
- Not runnable here: the video pipeline (needs FFmpeg video and BASS). Covered by the
  manual smoke below.

## Manual smoke (Windows, native DLLs)

1. Play a video, drag the seek bar, hold still for 2 s (playback carries on from the drag
   position), then release without moving: no jump; playback just continues. Repeat with
   `--software`.
2. Same with an MP3.
3. Drag, hold, move a little more, release: playback continues from the new position.
4. A quick drag and release: playback continues from the release point; the picture may
   change once from the preview frame to the exact frame (non-goal), never backwards after
   playback starts.
5. Drag while paused, release: stays paused at the new position.
6. CDG (MP3+G): drag, hold, release; lyrics match the audio position.
7. Regression: a click (no drag) on the seek bar seeks once; Stop during a drag stops;
   loading another file mid-drag does not misbehave.
