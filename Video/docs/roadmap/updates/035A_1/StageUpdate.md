# Stage Update

Stage: 035A.1 - Control-owned seek scrubbing
Date: 2026-10-17

## Status: IMPLEMENTED - manual Player smoke pending

Delivered: see the stage file.

Tests: 7 cases in `SeekScrubTests.cs` and 1 facade case in `PreparedNextTests.cs`
(`Scrub_LoadingMidDrag_DoesNotResumeTheNewItem`), all Portable.

## Decisions made without the owner

- **Playback is held during a drag.** Audio is silent while the mouse is down, as in most
  players, and resumes on release from where you let go.
- The hold is not reported as a pause, so a host's Play/Pause button does not flicker
  during a drag. `State` reads `Paused` while the hold lasts.
- New API instead of guessing: without a mouse-up signal the control cannot tell "drag
  paused" from "seek, play, then seek to the same spot again".
- The `IMediaControlSession` members have default implementations, as in 034A, so other
  implementations keep compiling.
- Seek then Play on a video is now race-free in general, not only for scrubs: Play during a
  pending seek is applied when the seek settles. A Play issued at the very end after a
  seek to the end no longer restarts from the beginning; the seek target wins.

## Verification

- Linux: full-solution Release build clean; Portable 728/728.
- Not runnable here: the video pipeline change (needs FFmpeg video and BASS). Covered by
  the manual smoke below.

## Manual smoke (Windows, native DLLs)

1. Play a video, drag the seek bar, hold still for 2 s, then release: playback resumes
   from the release point with no jump back. Repeat with `--software` and with an AV1 file.
2. Same with an MP3: no sound while dragging; on release it plays from the release point
   without replaying the last second.
3. A quick drag and release without holding still: one settle at the target; the picture
   may change once from the preview frame to the exact frame (non-goal), never backwards
   in time after playback starts.
4. Drag while paused, release: stays paused at the new position.
5. Drag, and press Space or Pause before releasing (if the host allows): stays paused.
6. CDG (MP3+G): drag, hold, release; lyrics match the audio position.
7. Regression: click (no drag) on the seek bar seeks once and keeps playing; Play button
   state does not flicker during drags; Stop during a drag stops.
