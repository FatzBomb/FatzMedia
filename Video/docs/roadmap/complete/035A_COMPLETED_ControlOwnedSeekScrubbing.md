# Completed Stage 035A - Control-Owned Seek Scrubbing

Prepared: 2026-10-17
Status: Completed 2026-10-18. Slice 1 (control-owned drags: playback carries on, no jump on
release) and slice 2 (optional silent drag); owner smoke passed 2026-10-17 and 2026-10-18
(`updates/035A_1`, `updates/035A_2`).

Slice 1 history: owner report
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

## Slice 2 Acceptance Criteria - Silent Drag

Owner request (2026-10-18): an option for a silent drag, audible again when the drag pauses,
"but not garbage while dragging".

- Public `MediaScrubAudio` (`Audible` default, `SilentWhileMoving`);
  `MediaControlPlayer.ScrubAudio`, and `IMediaControlSession.ScrubAudio` with a default that
  stays `Audible`.
- `SilentWhileMoving`: every seek during a scrub mutes before it moves the media, so nothing
  of the new position is heard while the drag moves. Audio comes back with a 20 ms ramp
  once no scrub seek has arrived for 120 ms (the drag rests;
  250 ms in the first build, shortened at the owner's smoke), or at once when the scrub ends
  or the media loads or closes.
- The mute is its own attenuation in `MediaEffects`: `Volume`, `VolumeChanged`, fades and
  normalization are untouched, and a volume change during the mute is applied on unmute.
  It works on the channel volume attribute, which BASS applies after the playback buffer, so
  it is heard at once (an FX stage would only act on audio not yet buffered).
- Both BASS paths: plain audio (`AudioEngine`, including CD+G songs) and video soundtracks
  (`VideoAudioEngine`).
- Player: **Silent Drag** check box in the Audio flyout, on by default.
- Focused tests: the quiet-period timer with a manual clock, and the facade (mute before the
  drag seek, restore on release, default `Audible`, load mid-drag).

## Explicit Non-Goals

- Exact positions for drags. A drag resumes from the previewed frame, up to 2 s from the
  mouse position (keyframe-style seeking, as in most players). Clicks stay exact.
- Audio scrubbing sound (playing short snippets while dragging). Slice 2 makes it silent
  instead, as an option.
- Changing the seek pump's adaptive timing (015A).
