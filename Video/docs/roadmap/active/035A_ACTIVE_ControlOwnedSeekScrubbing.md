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

- `MediaControls.BeginScrub()`, `EndScrub()`, and `IsScrubbing`; `MediaControlPlayer`
  `BeginScrub()` / `EndScrub()` on the control queue; `IMediaControlSession` members with
  default implementations that return false.
- `BeginScrub` while playing holds playback (engine pause) without `MediaPaused`,
  `MediaStarted`, or `StateChanged`; seeks in between only move the position and preview.
  `EndScrub` resumes once, from the last target, only if playback was held.
- `Play`, `Pause`, or `Stop` during a scrub, and a media load or close, cancel the resume;
  host `Play`/`Pause`/`Stop` are reported as usual.
- `VideoPipeline.Play()` during a queued or settling seek marks playback as started and lets
  the exact settle resume from the target.
- The Player test app's seek bar calls `BeginScrub` on mouse down and `Seek` then
  `EndScrub` on release.
- Focused tests for the scrub state machine and the facade (Portable).

## Explicit Non-Goals

- Exact frames during a fast drag. Preview frames during an active scrub stay approximate
  (nearest decoded frame) for responsiveness; the release settles exactly, so a fast drag
  released without a pause can still change the picture once.
- Audio scrubbing sound (playing short snippets while dragging).
- Changing the seek pump's adaptive timing (015A).
