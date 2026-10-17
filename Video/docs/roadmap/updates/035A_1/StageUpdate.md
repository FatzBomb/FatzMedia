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

## Revision after the third smoke (same day)

The owner reported that after a drag stopped, the picture paused on the shown frame for
about a second, then played from a second or two ahead or behind. Cause: drag previews show
the nearest decoded frame within 2 s of the mouse position (`PreviewMaxFrameDistanceSeconds`)
for responsiveness, and the settle then sought exactly to the mouse position, decoding from
the keyframe (the pause) and resuming there (the jump). Now an active scrub settles on the
frame its last preview showed (`VideoSeekPolicy.GetScrubSettleTarget`), so playback carries on
from the picture on screen. A lone click is unchanged and exact. During a scrub,
`SeekCompleted` accepts whatever position the seek settled on.

## Decisions made without the owner

- **Drags resume from the shown frame, not the mouse position**, up to 2 s apart. The seek
  bar thumb moves to the frame's time once the drag settles. The alternative, decoding
  exactly to the mouse position before resuming, keeps the pause and still changes the
  picture.

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

1. Play a video, drag the seek bar, hold still: playback carries on from the frame on
   screen, with no pause of about a second and no jump. Release without moving: no jump.
   Repeat with `--software` and with a long-GOP file.
2. Same with an MP3.
3. Drag, hold, move a little more, release: playback continues from the new position.
4. A quick drag and release: playback continues from the release point; the picture may
   change once from the preview frame to the exact frame (non-goal), never backwards after
   playback starts.
5. Drag while paused, release: stays paused at the new position.
6. CDG (MP3+G): drag, hold, release; lyrics match the audio position.
7. Regression: a click (no drag) on the seek bar seeks once; Stop during a drag stops;
   loading another file mid-drag does not misbehave.
