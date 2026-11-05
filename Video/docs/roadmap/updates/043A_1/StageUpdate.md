# Stage Update

Stage: 043A.1 - Player modes at creation (Full, Background, Audio)
Date: 2026-10-23

## Status: IMPLEMENTED - pending manual smoke

Owner decision (2026-10-23): one `Mode` per player, each mode loads only what it needs. This
slice sets the mode at creation; switching on the fly is slice 2.

Delivered:

- Public API:
  - `MediaPlayerMode { Full, Background, Audio }` and `MediaPlayerConfiguration.Mode`
    (default `Full`).
  - `MediaControlPlayer.Mode`, `AutoBackground` and `TransparentBackground`. The same three are
    on `IMediaControlSession`, with default implementations.
  - `MediaInputSource.FromColor(uint argb, string? displayName = null)`.
- `MediaPlayerModePolicy` (internal, pure): the spec table. It says how each mode opens each
  file kind (normal, audio only, reject) and which source kinds it accepts. It also says
  whether the mode uses presentation, audio output and backgrounds, and whether it forces
  loop.
- Full mode: unchanged with the defaults.
- Background mode:
  - `SilentAudioEngine` replaces `AudioEngine`, so BASS is never initialized.
  - No audio device monitor.
  - `VideoPipeline` decodes the picture only and runs on the playback clock.
  - Only the video pipeline is attached.
  - `LoopMedia` is forced on: `false` is ignored with a Warning.
  - Accepts video, images and BGRA sources. Rejects audio files, MIDI, CD+G, MP3+G and
    network sources.
- Audio mode:
  - `AudioOnlyPresentationCoordinator` replaces the platform coordinator. There is no
    renderer, GPU device or surface, and surface attach returns false with a Warning.
  - There is no video pipeline.
  - Video files play their soundtrack through the new `SoundtrackAudioEngine`, with no video
    decoder. MP3+G plays its audio only.
  - Rejects images, CD+G graphics, BGRA sources and video files without an audio stream.
  - Backgrounds are never loaded.
- `TransparentBackground`:
  - Where nothing is drawn, the composition clears to Transparent instead of Black.
  - The CD+G background opacity is forced to 0 while it is on.
- Rejected loads return false. They raise a `DiagnosticRouter` Warning in category
  `PlayerMode` and an `UnsupportedFormat` error event.

## Decisions

See "Slice 1 decisions" in `active/043A_ACTIVE_PlayerModes.md`. In short:

- **Audio mode for video files.** A `SoundtrackAudioEngine` wraps `VideoAudioEngine` and
  pumps it, as a second `AudioPipeline`. Two alternatives were rejected:
  - A video-disabled `VideoPipeline`: too risky in 2,300 frame-centred lines.
  - The 033A WAV conversion: it converts the whole film first.

  The file opens as plain audio content, with tempo, pitch, EQ, fades and normalization.
  End of playback: the position reaches the duration, or stops moving for 1 s while playing.
- **The no-op coordinator is in Core** (`Controllers/AudioOnlyPresentationCoordinator`). The
  platform factory is not asked for a coordinator in Audio mode.
- **Background mode audio engine.** It uses a new `SilentAudioEngine`, not `ClockAudioEngine`,
  whose setters throw.
- **Mode-dependent construction** is decided in one place:
  - `CreateSessionGraph`, with the helpers in `MediaControlPlayer.Modes.cs`.
  - The DI registration of `IAudioEngine`.
  - The `PlaybackCoordinator` constructor.

  `Reset` with another configuration already builds another mode, which slice 2's
  `SetModeAsync` can reuse.
- **Host options survive `Reset`.** `AutoBackground` and `TransparentBackground` values the host
  set are kept. Options never set leave the defaults untouched.

## Not in this slice

- `SetModeAsync` (slice 2).

The Avalonia test player's `--mode=full|background|audio` option was added on the session branch
after merging this slice; the smoke steps below use it.

## Verification (Linux sandbox)

- `EnableWindowsTargeting=true dotnet build FatzBomb.MediaControl.sln -c Release`: 0 warnings,
  0 errors.
- Tests:
  - Portable: 1024 pass, 63 of them new: `PlayerModeTests`, plus
    `MediaPresentationCoordinatorTests.TransparentBackground_ClearsWhereNothingIsDrawnToTransparent`.
  - Linux: 51 pass.
  - Avalonia: 14 pass.
- `dotnet format --verify-no-changes`: clean.
- Boundary scripts:
  - The sandbox refused to run `pwsh` here.
  - The Core forbidden-pattern list from `check-core-boundary.ps1` was checked by search, with
    no matches.
  - No project references changed, and no adapter files changed.
- Not runnable here: BASS/FFmpeg playback, the Windows test project, D3D11 and WPF.

## Manual smoke (Avalonia test player, then Windows WPF player)

Run the Avalonia test player with `--mode=...` (once added) and a folder with:

- a music video (MP4 with audio);
- a silent video;
- an MP3;
- an MP3+G pair;
- a MIDI file;
- a JPG.

1. **Full (`--mode=full` or no option).** Everything plays as before:
   - The video plays with sound.
   - The MP3+G shows its graphics.
   - MIDI lyrics appear.
   - An MP3 with a sidecar JPG shows the background.
2. **Background (`--mode=background`):**
   1. Load the music video. The picture plays and loops, with no sound. No BASS initialization
      line appears in the log ("Global BASS initialized ..." is absent).
   2. Loop is on and cannot be turned off. Unticking Loop logs "Background mode always loops".
   3. Load the JPG. It shows.
   4. Load the MP3, the MP3+G and the MIDI file. Each load fails with "Background mode does not
      accept ...".
   5. If the player can load a solid colour (`MediaInputSource.FromColor`), it fills the output.
3. **Audio (`--mode=audio`):**
   1. The surface stays empty, and the log shows "Audio mode has no presentation; surface ...
      was not attached".
   2. Load the music video. Its soundtrack plays. Check seek, pause and resume, tempo, pitch,
      volume and EQ. At the end playback stops (Loop off) or wraps without a gap (Loop on).
   3. Load the MP3+G. Its audio plays; there are no graphics.
   4. Load the MIDI file. It plays.
   5. Load the JPG and the silent video. Both are rejected with a diagnostic.
4. **TransparentBackground** (WPF composited or Avalonia, with a coloured app layer below the
   surface):
   1. Turn it on and play the MP3+G. The CD+G background colour is transparent, so the app layer
      shows through.
   2. Play a 4:3 video in a 16:9 surface. The letterbox bars are transparent.
   3. Turn it off. The output is black again, and the CD+G opacity returns to its previous value.
5. **AutoBackground = false** (Full mode): an MP3 with a sidecar JPG plays with no background.
   `Load(mp3, jpg)` still shows the explicit JPG.

## Linux run (2026-11-05)

Environment: Linux sandbox, BASS 2.4 for Linux (+ BASS_FX, BASSFLAC), FFmpeg 9.0 LGPL, FatzAudio 1.0.0-rc.1
built from source (null output for the FatzAudio engine), PulseAudio null sinks with ALSA routed to it;
media from `scripts/setup-linux-native.sh` (`/opt/fatzbomb-native/media/formats`). Opt-in tests:
`FATZBOMB_LINUX_BASS_DIR`, `FATZBOMB_LINUX_FFMPEG_DIR` (see `LinuxNativeTestPlayer`).

`LinuxPlayerModesNativeTests`, headless on a frame sink, 4 of 4 pass:
- Background mode refuses an MP3 ("Background mode does not accept"), presents a picture, and loops a music
  video silently with no "Global BASS initialized".
- Audio mode refuses a picture ("Audio mode does not accept") and a silent video ("no playable audio
  stream"), and plays a music video's sound with nothing presented.
- `SetModeAsync` Full -> Audio while playing, Audio -> Full while paused: position (within 1.5 s playing,
  0.5 s paused), state, volume 0.6, tempo 110 and pitch +2 kept; video returns in Full; stopped stays
  stopped. "Audio mode has no presentation; surface 'primary' is kept ..." logged.
- Full -> Background loops the video (Loop forced on); an MP3 switched to Background closes with "Background
  mode could not reopen ...".
- Still Windows only: the WPF and Avalonia surfaces during a switch (no black or stale frame), MP3+G graphics
  after Audio and back, TransparentBackground and AutoBackground in the players, rapid switching under the UI.
