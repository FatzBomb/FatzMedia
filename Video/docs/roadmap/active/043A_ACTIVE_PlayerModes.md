# Active Stage 043A - Player Modes (Full, Background, Audio)

Prepared: 2026-10-23
Status: slice 1 implemented, pending manual smoke (`updates/043A_1`). Slice 2 not started.
Owner decision (2026-10-23): one `Mode` per player, each mode loads only what it needs, and modes
can be switched on the fly. No CD+G mode: `AutoBackground` and `TransparentBackground` are options of
Full mode instead. Prepares for 037A, where an Audio-mode player later attaches to a FatzAudio deck.

## Modes

| Mode | Loads | Accepts | Rejects (load returns false + diagnostic) |
|---|---|---|---|
| **Full** | today's player | everything | - |
| **Background** | video decoding, renderer, surfaces; no BASS, no audio device monitor, no CD+G/subtitle/MIDI playback | video (picture only, no audio decoded), images, BGRA sources (including a solid colour) | audio-only files, CD+G, MIDI, network audio |
| **Audio** | audio engine only; no presentation coordinator, no GPU device, no surfaces, no video decoder | anything with audio: audio files, MIDI, the soundtrack of a video file, the audio of MP3+G (graphics ignored) | images, BGRA sources, video files with no audio stream |

Background mode always loops (`LoopMedia` reads true and cannot be turned off) and never has
audio, so `Volume`/EQ/fades are accepted and ignored. Audio mode ignores surfaces: attach calls
return false with a diagnostic.

## Public API (slice 1)

- `public enum MediaPlayerMode { Full, Background, Audio }`.
- `MediaPlayerConfiguration.Mode` (default `Full`): the mode the player is created in.
- `MediaControlPlayer.Mode` (and on `IMediaControlSession` with a default implementation returning
  `Full`, so other implementers do not break).
- `MediaControlPlayer.AutoBackground` (default `true`): Full mode searches for sidecar/archive
  backgrounds. `false` = no search; explicit `Load(path, backgroundPath)` still works.
- `MediaControlPlayer.TransparentBackground` (default `false`): where nothing is drawn (no background,
  CD+G background colour) the output is transparent instead of black, so an app layer below shows
  through on hosts that composite (WPF composited, Avalonia, frame sinks). It drives the CD+G
  background opacity and the composition clear mode.
- `MediaInputSource.FromColor(uint argb)`: a solid-colour source (small BGRA image scaled by the
  existing image path), for Background mode or any image slot.

## Slices

- **Slice 1 - modes at creation (light loading).** `Mode` from the configuration; Background and
  Audio skip what they do not need (table above); `AutoBackground`, `TransparentBackground`,
  `FromColor`. Focused tests with the existing test doubles for the pure decisions (what a mode
  accepts, what it creates).
- **Slice 2 - switching on the fly.** `Task<bool> SetModeAsync(MediaPlayerMode)`: builds what the new
  mode needs and frees what it no longer needs (default; no "keep warm" option yet), then reopens the
  current source at its position and transport state. First version may have a short gap, like a
  seek; Full <-> Audio without an audio gap is a later refinement if the app needs it.
- **Later (037A):** an Audio-mode player can attach to a FatzAudio deck instead of opening its own
  output.

## Slice 1 decisions

- **One mode policy.** `MediaPlayerModePolicy` (internal, pure) holds the spec table: how each mode
  opens each file kind (normal, audio only, reject), which source kinds it accepts, and whether it
  uses presentation, audio output, backgrounds and forced loop. The engine and the player read it;
  the tests check it directly.
- **Mode-dependent construction in one place.** `MediaControlPlayer.CreateSessionGraph` builds
  everything from the configuration, including `Mode`; the mode-specific choices are in
  `MediaControlPlayer.Modes.cs` (presentation coordinator, audio device monitor, forced loop,
  the host's background options), `MediaControlServiceProvider` (audio engine) and the
  `PlaybackCoordinator` constructor (which pipelines are attached). `Reset` with another
  configuration already rebuilds the graph in another mode, so slice 2's `SetModeAsync` is a
  rebuild plus a reopen at the saved position. The player keeps the configuration it was built
  with for that.
- **Audio mode, video files: a soundtrack engine, not a video-disabled `VideoPipeline`.** New
  `SoundtrackAudioEngine` wraps the `VideoAudioEngine` that video playback already uses (FFmpeg
  decode into a BASS push stream, with tempo, pitch, equalizer, fades and normalization) and runs
  its decode pump. Audio mode attaches it as a second `AudioPipeline`, selected while a video
  file is open, so the file opens as plain audio content (`MediaContent.Audio`, no
  `player.Video`). No `VideoDecoder` and no `VideoPipeline` exist in Audio mode. Rejected
  alternatives: a video-disabled `VideoPipeline` (2,300 lines built around decoded frames, too
  risky to make decoder-optional), and the 033A FFmpeg-to-WAV fallback through `AudioEngine`
  (it converts the whole file first: about 21 MB per minute and a long wait for a film).
  The push stream never ends by itself, so the engine ends playback when the position reaches
  the duration or stops moving for 1 s while playing (the audio ran out before the container's
  duration). Gapless loop wraps are reported like plain audio's.
- **Audio mode presentation.** New internal `AudioOnlyPresentationCoordinator` in Core: no
  renderer, GPU device or surfaces. `AttachPresentationSurface` returns false with a
  `PlayerMode` Warning; the other calls do nothing. The platform factory's
  `CreatePresentationCoordinator` is not called.
- **Audio mode content.** MP3+G opens its audio only (no CD+G pipeline). No subtitle pipeline
  is built, so MIDI lyrics are not extracted. Backgrounds are never searched (`AutoBackground`
  reads false) and an explicit background is ignored with a Warning; the load still succeeds.
- **Background mode audio.** `MediaControlServiceProvider` registers a new `SilentAudioEngine`
  (no-op `IAudioEngine`) instead of `AudioEngine`, so `BASS_Init` and the MIDI soundfont setup
  never run, and the player does not resolve or start the audio device monitor. `ClockAudioEngine`
  was not reused because its setters throw `NotSupportedException`. `VideoPipeline.Open` skips
  `VideoAudioEngine` in Background mode, so the video runs on `PlaybackClock`. The BASS library
  itself is still loaded by the platform's native bootstrap (per process, shared by all players);
  only plugin loading runs, which needs no device.
- **Background mode content.** Only the video pipeline is attached (no audio, CD+G or subtitle
  pipelines); embedded and sidecar subtitles of a video are not loaded. Network sources are
  rejected.
- **Forced loop.** `MediaControls.ForcesLoop` (internal) turns `LoopMedia` on and ignores
  `LoopMedia = false` with a Warning, so `player.Controls.LoopMedia` cannot bypass it either.
- **Rejected loads** return false, raise a `DiagnosticRouter` Warning in category `PlayerMode`
  ("Background mode does not accept audio files; song.mp3 was not loaded.") and an
  `UnsupportedFormat` error event. A video file with no audio stream in Audio mode gets its own
  message.
- **`AutoBackground`** maps to the engine's existing `BackgroundAutoDetect`; a value the host
  sets is kept across `Reset`. Never set, the graph keeps its default (true).
- **`TransparentBackground`** switches the two content clears of `MediaPresentationCoordinator`
  that were hard-coded Black (the software composition frame and the hardware video frame) to
  Transparent, and forces the CD+G background opacity to 0 through `CdgVisuals` without changing
  `CdgVisuals.BackgroundOpacity`, which applies again when it is turned off. The other clears were
  already Transparent. No adapter change was needed. Default off: identical output.
- **`FromColor`** creates an 8x8 straight-alpha BGRA image (`MediaInputSource.SolidColorSize`).
- **`IMediaControlSession`** gets `Mode`, `AutoBackground` and `TransparentBackground` with default
  implementations (Full, true and false, setters ignored).
- **Not in this slice:** the Avalonia test player's `--mode` option (added on the session branch
  after merge, see `updates/043A_1`).

## Explicit non-goals

No CD+G mode, no mixer in this stage (FatzAudio, 037A), no playlist logic, no adapter changes beyond
what `TransparentBackground` needs to be visible.

## Acceptance

- A player created in Audio mode never creates a presentation coordinator or GPU device and never
  opens a video decoder; a video file plays its soundtrack.
- A player created in Background mode never initializes BASS or the audio device monitor; a video
  file plays its picture looping, on the clock.
- Rejected loads return false with a clear diagnostic.
- Full mode behaves exactly as before with the defaults.
- Release build warning-clean; Portable, Linux, Avalonia tests pass; `dotnet format` clean.
