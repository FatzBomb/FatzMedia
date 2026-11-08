# Stage Update

Stage: 043A.2 - Switching player modes on the fly
Date: 2026-10-23

## Status: IMPLEMENTED - pending manual smoke

Owner decision (2026-10-23): modes can be switched on the fly. Slice 1 set the mode at creation;
this slice adds the switch.

Delivered:

- Public API:
  - `MediaControlPlayer.SetModeAsync(MediaPlayerMode)` (`Task<bool>`).
  - `MediaControlPlayer.ModeChanged`
    (`EventHandler<MediaValueChangedEventArgs<MediaPlayerMode>>`).
  - Both are on `IMediaControlSession`, with default implementations: `SetModeAsync` returns
    `true` only for the current mode, and `ModeChanged` is never raised.
  - `Mode` now reads the current mode.
- The switch:
  - It runs on the control queue. Calls made during it run after it, on the new graph.
  - It builds the new mode's graph and frees the old one, through the same path as `Reset`
    (`RebuildSessionGraph`), inside the player's diagnostic owner scope.
  - It reopens the open media at its position, in its transport state (Playing, Paused,
    Stopped).
  - It keeps the loaded source, the explicit background, the host's `LoopMedia`, the effects,
    the sync offsets, the display options and the prepared-next item.
- The same mode returns `true` and rebuilds nothing.
- Surfaces:
  - They are held while the graph is rebuilt and attached again to the new graph, at their
    latest size.
  - In Audio mode they are kept, not rendered, so the host can resize a hidden surface.
- Avalonia test player: a Mode combo box per player.

## Decisions

See "Slice 2 decisions" in `active/043A_ACTIVE_PlayerModes.md`. In short:

- **Prepared-next survives the switch.**
  - The slot belongs to the player, not to the graph, and its analysis is plain data.
  - An item the new mode does not accept fails at transition, with the mode's normal rejection.
    This is already the case for an item prepared in that mode.
  - `Reset` still clears the slot.
- **Unaccepted source.**
  - The switch reopens the source through the normal load, so the mode's own rules decide.
  - If the reopen fails, the switch still returns `true` and the player ends closed.
  - A `PlayerMode` Warning names the file, next to the mode's own rejection Warning and error
    event.
  - `MediaClosed` and `StateChanged` (to `Null`) come from the close, as for any close.
- **Surfaces.**
  - Audio mode now accepts attach calls: they return `true`, with an Info diagnostic, and the
    surface is only remembered. In slice 1 they returned `false` with a Warning.
  - With `false`, adapters would never detach, and a stale handle would be attached on
    return.
  - Host surface calls during a rebuild only update the player's set.
- **Events.**
  - The reopen is a real close and load: `MediaClosed`, `MediaLoaded` (with the new mode's
    content) and the state changes.
  - Then `ModeChanged`, once, queued after them.
  - `LoopMediaChanged` is raised once when entering or leaving Background changes the visible
    loop value.
  - Settings that did not change raise nothing: the new graph gets them before its events are
    forwarded.
- **Loop.**
  - `MediaControls.HostLoopMedia` keeps the host's own value while Background forces looping.
  - Leaving Background restores the host's value.
- **Paused restore.** Freshly opened audio cannot always pause before playing. In that case it
  plays muted for an instant, then pauses.
- **Build failure.** The switch rebuilds the previous mode, reopens the media there and returns
  `false`.
- **Dispose or Reset during a switch.** They wait for the switch, then run. The switch never
  takes the admission gate they hold.

## Not in this slice

- Gapless Full <-> Audio: there is a short gap, like a seek.
- A "keep warm" option: the old mode's resources are always freed.

## Verification (Linux sandbox)

- `EnableWindowsTargeting=true dotnet build FatzBomb.MediaControl.sln -c Release`: 0 warnings,
  0 errors.
- Tests:
  - Portable: 1064 pass. The 12 new tests are in `PlayerModeSwitchTests`.
    `PlayerModeTests.AudioMode_UsesNoPresentationCoordinatorAndKeepsSurfaces` replaces the slice 1
    surface-rejection test.
  - Linux: 51 pass.
  - Avalonia: 14 pass.
- `dotnet format --verify-no-changes`: clean.
- Boundary scripts (`check-core-boundary.ps1`, `check-adapter-boundary.ps1`) pass.
- Not runnable here: BASS/FFmpeg playback, the Windows test project, D3D11 and WPF. The real
  engine's transport after a reopen (pause before play, seek while stopped) is covered only by
  the test doubles.

## Manual smoke (Avalonia test player, then Windows WPF player)

Use the Mode box of a player, with the same test folder as slice 1:

- a music video (MP4 with audio);
- a silent video;
- an MP3;
- an MP3+G pair;
- a JPG.

Keep the log open.

1. **Full -> Audio -> Full while playing.**
   1. Start in Full and play the music video. Seek to the middle and set volume, pitch and tempo.
   2. Switch to Audio. After a short gap the soundtrack continues from the same position, with
      the same volume, pitch and tempo. The surface no longer updates. The log shows the Info line
      "Audio mode has no presentation; surface 'primary' is kept ...".
   3. Resize the window while in Audio mode.
   4. Switch back to Full. The video comes back at the current position, playing, and fills the
      resized surface at the new size.
2. **Paused and stopped.**
   1. Pause the video and switch to Audio and back. Each time it stays paused at the same
      position, with no audible blip.
   2. Stop and switch. It stays stopped.
3. **Background and loop.**
   1. In Full mode, with Loop unticked, play the music video and switch to Background. The
      picture continues looping, with no sound, and the Loop box is ticked.
   2. Switch back to Full. Loop is unticked again, and the sound returns.
   3. In Background, tick Loop (it stays ticked), then switch to Full. Loop stays ticked.
4. **Unaccepted source.**
   1. Play the MP3 in Full mode and switch to Background.
   2. The player is closed. The log shows "Background mode does not accept audio files ..." and
      "Background mode could not reopen song.mp3; the player is closed.". The Mode box shows
      Background.
   3. Play the JPG in Full mode and switch to Audio. The JPG is closed with the same kind of
      Warning.
   4. Play the silent video in Full mode and switch to Audio. It is closed: "Audio mode found no
      playable audio stream ...".
5. **MP3+G.** Play the MP3+G in Full mode and switch to Audio. The audio continues without
   graphics. Switch back to Full: the graphics return in sync.
6. **Background mode resources.** Start with `--mode=background` and switch to Full. The log shows
   BASS initializing for the first time ("Global BASS initialized ..."). Sound works.
7. **Quick switches.**
   1. Switch modes several times quickly, and press Play/Pause during a switch. Every action
      applies after the switch, and nothing hangs.
   2. Close the window during a switch. It closes without hanging.
8. **WPF host (Windows).** If the WPF player gains a mode switch, repeat step 1 with a native
   HWND surface and a WPF-composited surface. The surface comes back after Audio mode, with no
   black or stale swap chain.

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
