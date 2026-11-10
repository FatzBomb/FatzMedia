# Stage Update

Stage: 037A.4 - MIDI, tracker modules and network audio on the FatzAudio engine
Date: 2026-11-10

## Status: implemented, pending manual smoke (one small code change; the rest was already in slice 2)

Slice 4 was planned as soundfont/DLS loading, MIDI texts and marks, a tracker timeline and network playback
for the FatzAudio engine. Checked against the code on 2026-11-10:

| Item | State |
|---|---|
| Soundfont and DLS loaded directly | Done in slice 2: `MidiSoundfontPath` goes to `SoundFont.FromFile` (SF2, SF3, DLS); without it FatzAudio uses the system bank (`gm.dls`). New here: with the FatzAudio engine, `PlayerServiceProvider` no longer sets up BASSMIDI's soundfont, so the DLS-to-SF2 conversion (ENG-028, ENG-030) never runs; it still runs when BASS plays files or when the FatzAudio engine fails to start and BASS takes over. |
| MIDI texts and marks | Done in slice 2: `FatzAudioMidiEventReader` (lyrics, text, track names, copyright, key and time signatures, tempo). |
| Tracker timeline | Nothing to build. On BASS, `TrackerPositionTimeline` maps order/row back to the first pass after a module jumps back to its restart order. FatzAudio plays a module once through with exact seeks and does not follow that jump, so its clock never goes back and needs no mapping. No public API exposes order and row, so `GetTrackerTime` is not wired. |
| Network audio | Decision: one network path. With the FatzAudio engine, plain http audio already opens through FFmpeg (slice 2), like https and HLS: certificate checks, retries, buffering events and in-stream titles all live there. FatzAudio's own `OpenUrl` is not used. The FFmpeg path plays into the soundtrack engine, which slice 5 moves to FatzAudio, so network audio leaves BASS with slice 5. |

## Tests

No new tests: the change is a startup branch that needs BASS to observe. Release build, the full test suite,
the boundary scripts and the format check pass (run with slice 5).

## Manual smoke (owner)

With `--audio=fatz`:

1. A `.kar` and a `.mid`: they play with the system bank, lyrics as before. The Log has no `BASSMIDI` lines and
   `%LOCALAPPDATA%\FatzMedia.Video\soundfonts` gets no new `gm.dls.sf2`.
2. A `.mod` or `.xm` with a loop back to its restart order: it plays to the end once and the position bar never
   jumps back.
3. A plain `http://` radio stream: plays, and titles appear (now through FFmpeg into FatzAudio, see slice 5).
