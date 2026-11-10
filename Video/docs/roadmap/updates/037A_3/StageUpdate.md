# Stage Update

Stage: 037A.3 - CD+G drawn by FatzAudio (FatzAudio engine; BASS unchanged)
Date: 2026-11-10

## Status: implemented, pending manual smoke

With `--audio=fatz` (`MediaPlayerConfiguration.AudioEngineFactory = new FatzAudioEngineFactory()`), CD+G
graphics are decoded and drawn by FatzAudio's CD+G surface instead of our `CdgEngine`. The picture reaches
every renderer (Direct3D11, Software, Vulkan) through the same indexed snapshot as before, so no renderer
changed. Without the factory (BASS, the default) nothing changes.

## Design

| Part | Where | What |
|---|---|---|
| `ICdgEngineSource` (internal) | Core, `Core/Cdg` | An audio engine that brings its own CD+G engine. `PlayerServiceProvider` asks the player's audio engine for it and falls back to `CdgEngine`. |
| `FatzAudioCdgEngine` | `FatzMedia.Video.Audio` | `ICdgEngine` + `ICdgSnapshotSource` over a FatzAudio `CdgSurface` in `Indexed8` (300x216 colour indices and a 16-colour palette, the classic picture). |
| Colours | `FatzAudioMappings.ToCdgPaletteArgb` / `ToCdgBackgroundWashArgb` | FatzAudio's palette with our background opacity and override applied exactly as `CdgColorManager` applies them. |

Decisions:

- **A carrier stream, not the playing stream.** The `.cdg` bytes are loaded into a FatzAudio push stream that
  never gets audio and is never played; the surface draws at the time we pass. So the song is not parsed twice,
  a stand-alone `.cdg` (our own clock) needs no FatzAudio request 10, and the CD+G does not care whether the
  song plays from a file, an archive entry or an FFmpeg conversion.
- **Same time, same pack.** The time is still the audio position plus our CD+G offset (the presentation loop
  is unchanged); FatzAudio's own CD+G offset stays 0. FatzAudio shows ceil(t x 300) packs and we show floor, so
  the engine takes our pack count and asks FatzAudio for the middle of that pack: both engines show the same
  pack at every position (A/B compares like with like). A FatzAudio error while drawing keeps the last picture
  and is logged once per song.
- **Classic picture only.** Scaled presentation, effects, safety zones, the karaoke thread, render-ahead, the
  governor and the present-time draw (`GetClockAt`) belong with a scaled CD+G mode and its Player options; the
  classic picture costs about 0.3 ms per full frame and needs none of them. Not built.
- **MIDI lyrics stay on our renderers.** With the FatzAudio engine the lyrics already come from FatzAudio's
  MIDI reader (slice 2); FatzAudio's `LyricSurface` needs a new bitmap layer on three renderers and a style
  API, and does not block removing BASS. Parked for the slice 6 decision.
- **BASS keeps our `CdgEngine`.** No FatzAudio stream is opened beside BASS: BASS is on its way out (slice 6).
- **Gains:** FatzAudio applies the smooth-scroll offsets our engine ignores (ENG-029), and seeks replay at most
  10 s from its checkpoints.

## Tests

`FatzAudioCdgEngineNativeTests` (5, real FatzAudio library, nothing played): the picture matches our
`CdgStateMachine` replayed from the first pack, at 300 frames of steady play and 33 seeks (backward, forward,
across checkpoints, the last pack), at 40 % background opacity and with a background override; snapshots are
reused and their dirty rectangles cover every changed pixel; `Reset` is blank and the next render whole; a file
without a whole pack loads as an empty song; a second load replaces the first (archive entry). The service
provider test checks that the FatzAudio factory gives `FatzAudioCdgEngine`.

The FatzAudio native tests (`FatzAudioNativeGroup.CanRun`) now also run on Windows when `fatzaudio.dll` was
built from `Audio/`; Windows CI does not build it, so they return at once there, as before.

## Verification (Windows, this machine)

Release build 0 warnings; tests: Video 1601, Portable 1786, Linux 219 (all 40 FatzAudio tests ran on
Windows), Vulkan 103, Avalonia 52, all passed; the four boundary scripts and `dotnet format --verify-no-changes`
pass.

## Manual smoke (owner)

Build in Release and start the WPF player with `--audio=fatz`; compare each step with a run without it.

1. An MP3+G pair and a ZIP with MP3+CDG: the graphics appear and wipe in time with the singing; the Log says
   `Loaded CDG '...' into FatzAudio with N packets`.
2. Seek back and forward several times, pause and resume, loop a short song: the picture is right after
   every move (no leftovers from before the seek).
3. CD+G background opacity and background colour override in the player: the background changes as on BASS.
4. A stand-alone `.cdg` (no audio): it plays on the player's clock as before.
5. A disc you know scrolls lyrics smoothly line by line: it should now glide instead of jumping a tile.
6. `--players=2 --audio=fatz` with a CD+G song in each: both draw.
