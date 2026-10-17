# Active Stage 035A - Control-Owned Seek Scrubbing

Prepared: 2026-10-17
Status: Slice 1 implemented, pending manual smoke (`updates/035A_1`). Owner report
(2026-10-17 smoke): "Seeking still jumps at seek end", worst on video, also noticeable on
audio. The owner asked for the fix in the control, not the host.

## Why This Is Worth Doing

A seek bar sends a stream of seeks while the mouse is down and one more on release. The
control could not tell a drag from separate seeks:

- Video: when the drag paused long enough for the seek pump to settle, the pipeline
  resumed playback while the mouse was still down. Release then sought to the same target,
  so the picture jumped back by however long it had played.
- Audio: each seek restarted playback at once, so the release seek replayed the stretch
  heard since the last mouse move.
- `Play()` right after `Seek()` on a video could start at the old position until the seek
  worker stopped playback again, then jump.

Every host with a seek bar would have to work around this. The control owns transport
timing, so it owns the drag semantics.

## Slice 1 Acceptance Criteria

Revised after the owner's second smoke (2026-10-17): the first version held playback
during the drag. The owner prefers playback to carry on while the button is held, as
before, and it still jumped on release.

- `MediaControls.BeginScrub()`, `EndScrub()`, and `IsScrubbing`; `MediaControlPlayer`
  `BeginScrub()` / `EndScrub()` on the control queue; `IMediaControlSession` members with
  default implementations that return false.
- A scrub changes nothing about playback: it carries on, and each drag seek moves it.
- A seek during a scrub that repeats the scrub's previous seek target (within 1 ms) is
  ignored. That is the release seek at the last drag position, which used to restart
  playback from there. `MediaControlPlayer.Seek` still raises `SeekCompleted` for it, with
  the current position.
- A media load or close ends the scrub.
- When a video drag stops, playback resumes from the frame the drag preview put on screen,
  not from the exact mouse position (third smoke: "pause on our stopped frame, then play a
  second or two ahead or behind"). Drag previews show the nearest decoded frame, up to 2 s
  from the target; the exact settle on the target is what jumped. A lone click still seeks
  exactly. `SeekCompleted` during a scrub accepts the settled position.
- `VideoPipeline.Play()` during a queued or settling seek marks playback as started and lets
  the exact settle resume from the target.
- The Player test app's seek bar calls `BeginScrub` on mouse down and `Seek` then
  `EndScrub` on release.
- Focused tests for the scrub rules and the facade (Portable).

## Explicit Non-Goals

- Exact positions for drags. A drag resumes from the previewed frame, up to 2 s from the
  mouse position (keyframe-style seeking, as in most players). Clicks stay exact.
- Audio scrubbing sound (playing short snippets while dragging).
- Changing the seek pump's adaptive timing (015A).
