# Stage Update

Stage: 035A.2 - Silent drag
Date: 2026-10-18

## Status: IMPLEMENTED - owner smoke passed 2026-10-18

- 250 ms build: "looks (and sounds) good", but feedback felt late; shortened to 120 ms.
- 120 ms build: "I like that better." A slow drag lets audio through between moves, which the
  owner expects ("I am dragging slow enough to actually hear where I am"); fast drags are
  silent. Accepted; the option can be turned off.

Owner request: "option for silent drag if possible (still resumes at a drag pause, but not
garbage while dragging)".

Delivered: see the stage file, slice 2.

- `MediaScrubAudio` (`Audible`, `SilentWhileMoving`), `MediaControlPlayer.ScrubAudio`,
  `IMediaControlSession.ScrubAudio` (default `Audible`).
- `Controllers/ScrubAudioMuter.cs`: mutes on each scrub seek, unmutes after a 120 ms quiet
  period or when the scrub ends.
- `MediaEffects.SetScrubMuted`: the engine gets 0 while muted and the user volume afterwards;
  one lock orders it against `Volume` writes.
- `Core/Audio/BassChannelVolume.cs`: the channel volume set (ending any running slide) and
  slide shared by `AudioEngine` and `VideoAudioEngine`; both implement the new internal
  `IAudioVolumeSlide` for the unmute ramp.
- Player: **Silent Drag** in the Audio flyout, on by default.

Tests: 10 cases in `ScrubAudioMuterTests.cs` (manual clock) and 3 in
`MediaControlPlayerTests.cs` (`SilentScrub_MutesBeforeTheDragSeek_AndRestoresTheVolumeWhenTheScrubEnds`,
`AudibleScrub_IsTheDefault_AndNeverMutes`, `SilentScrub_LoadingMidDrag_LeavesTheNewMediaAudible`),
all in the shared folder, so they also run in Portable. Removing the mute call fails the
first facade test.

## Review follow-up (same day)

- A scrub seek that outlasts the quiet period (a network stream, a slow tracker seek) no
  longer lets audio through mid-drag: the quiet period counts from when the seek returns,
  and no unmute happens while one runs. The drag requests queued behind it had not reached
  the muter yet.
- The quiet-period timer callback catches everything and reports it as a diagnostic: an
  exception on a timer thread would end the process.

## Decisions made without the owner

- **Quiet period 120 ms** (first build 250 ms; owner smoke: "I don't 'feel' feedback as
  quickly as I think I should"). A drag sends a seek per mouse move, every 8-16 ms while it
  moves; 120 ms of none means the hand has stopped. A slow drag (pauses between moves longer
  than that) is heard in short bursts, which the owner wants; longer feels late. It is one constant
  (`ScrubAudioMuter.DefaultQuietPeriod`) if it needs tuning.
- **Audio returns where playback is, not where the drag stopped.** Playback carries on
  muted during the rest (slice 1: a drag never pauses playback), so it is heard from about
  120 ms past the rest position. Seeking back on unmute would add a seek and a glitch at
  the moment audio returns.
- **Mute is a step, unmute is a 20 ms ramp.** The mute lands right before a seek, which is a
  discontinuity anyway; a ramp there would let the start of the new position through.
- **Channel volume attribute, not a new FX stage.** BASS applies FX when it fills the playback
  buffer, so an FX unmute would be heard only after the buffered (muted) audio played out,
  up to the buffer length. The attribute acts at playback. User volume already lives there,
  so the mute is applied as "0 instead of the user volume" in `MediaEffects`, which owns
  that value.
- **Player default on.** The owner asked for it; other hosts keep `Audible` unless they opt in.
- Paused drags mute and unmute too; nothing is heard either way.

## Verification

- Linux: full-solution Release build clean (warnings as errors); `dotnet format
  --verify-no-changes` clean; Portable tests pass (832); boundary scripts pass.
- Not runnable here: BASS playback. Covered by the manual smoke below.

## Manual smoke (Windows, native DLLs)

1. MP3, Silent Drag on (default): drag the seek bar back and forth: no audio while moving.
   Hold still: audio comes back within about a tenth of a second, without a click. Move again:
   silent again. Release: audio plays on, no jump.
2. Same with a video (its soundtrack) and with an MP3+CD+G.
3. Change the volume slider mid-drag (keyboard or a second hand), release: the new volume.
4. Untick Silent Drag: drags sound as before (snippets from each position).
5. Quick click on the seek bar (no drag): audio plays from the new position on release (a
   click is a one-seek scrub; release ends it). Holding the button down after the click
   without moving: audio comes back after the quiet period.
6. Regression: fades (Next flyout transition), normalization, and volume behave as before;
   loading another file mid-drag plays it at the normal volume.
