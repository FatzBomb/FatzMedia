# Active Stage 043A - Player Modes (Full, Background, Audio)

Prepared: 2026-10-23
Status: slices 1-2 implemented, pending manual smoke (`updates/043A_1`, `updates/043A_2`).
Owner decision (2026-10-23): one `Mode` per player, each mode loads only what it needs, and modes
can be switched on the fly. No CD+G mode: `AutoBackground` and `TransparentBackground` are options of
Full mode instead. Prepares for 037A, where an Audio-mode player later attaches to a FatzAudio deck.

## Modes

| Mode | Loads | Accepts | Rejects (load returns false + diagnostic) |
|---|---|---|---|
| **Full** | today's player | everything | - |
| **Background** | video decoding, renderer, surfaces; no BASS, no audio device monitor, no CD+G/subtitle/MIDI playback | video (picture only, no audio decoded), network video (039A; loops where seekable), images, BGRA sources (including a solid colour) | audio-only files, CD+G, MIDI, network audio |
| **Audio** | audio engine only; no presentation coordinator, no GPU device, no surfaces, no video decoder | anything with audio: audio files, MIDI, network audio, the soundtrack of a video file or network video (039A), the audio of MP3+G (graphics ignored) | images, BGRA sources, video files with no audio stream |

Background mode always loops (`LoopMedia` reads true and cannot be turned off) and never has
audio, so `Volume`/EQ/fades are accepted and ignored. Audio mode does not render to surfaces. In
slice 1 attach calls returned false with a diagnostic; since slice 2 they are kept for a switch to
Full or Background (see "Slice 2 decisions").

## Public API (slice 1)

- `public enum MediaPlayerMode { Full, Background, Audio }`.
- `MediaPlayerConfiguration.Mode` (default `Full`): the mode the player is created in.
- `PlayerCore.Mode` (and on `IPlayerSession` with a default implementation returning
  `Full`, so other implementers do not break).
- `PlayerCore.AutoBackground` (default `true`): Full mode searches for sidecar/archive
  backgrounds. `false` = no search; explicit `Load(path, backgroundPath)` still works.
- `PlayerCore.TransparentBackground` (default `false`): where nothing is drawn (no background,
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
- **Mode-dependent construction in one place.** `PlayerCore.CreateSessionGraph` builds
  everything from the configuration, including `Mode`; the mode-specific choices are in
  `PlayerCore.Modes.cs` (presentation coordinator, audio device monitor, forced loop,
  the host's background options), `PlayerServiceProvider` (audio engine) and the
  `PlaybackCoordinator` constructor (which pipelines are attached). `Reset` with another
  configuration already rebuilds the graph in another mode, so slice 2's `SetModeAsync` is a
  rebuild plus a reopen at the saved position. The player keeps the configuration it was built
  with for that.
- **Audio mode, video files: a soundtrack engine, not a video-disabled `VideoPipeline`.** New
  `SoundtrackAudioEngine` wraps the `VideoAudioEngine` that video playback already uses (FFmpeg
  decode into a BASS push stream, with tempo, pitch, equalizer, fades and normalization) and runs
  its decode pump. Audio mode attaches it as a second `AudioPipeline`, selected while a video
  file is open, so the file opens as plain audio content (`MediaContent.Audio`, no video
  engine). No `VideoDecoder` and no `VideoPipeline` exist in Audio mode. Rejected
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
- **Background mode audio.** `PlayerServiceProvider` registers a new `SilentAudioEngine`
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
  `LoopMedia = false` with a Warning, so the player's `LoopMedia` cannot bypass it either.
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
- **`IPlayerSession`** gets `Mode`, `AutoBackground` and `TransparentBackground` with default
  implementations (Full, true and false, setters ignored).
- **Avalonia test player:** `--mode=full|background|audio` sets the mode of every player.

## Slice 2 decisions

- **API.** `PlayerCore.SetModeAsync(MediaPlayerMode)` returns `Task<bool>` and
  `ModeChanged` is an `EventHandler<MediaValueChangedEventArgs<MediaPlayerMode>>` (no new event
  args type). `IPlayerSession` gets both with default implementations: `SetModeAsync`
  returns `true` only for the current mode, and `ModeChanged` is never raised. `Mode` now reads the
  current mode.
- **One rebuild path.** `Reset` and `SetModeAsync` share `RebuildSessionGraph`: tear down
  (`TearDownSessionGraph`), `CreateSessionGraph` with the new configuration, reattach the surfaces,
  restart the audio device monitor. The switch builds `configuration with { Mode = mode }` from the
  configuration the player was built with, and the native library paths it kept. The rebuild runs
  inside `DiagnosticRouter.EnterOwner(this)` (ENG-025), so the new graph's threads, and the control
  queue a `Reset` creates, belong to the player.
- **Control queue.** The switch is a control operation (`EnqueueControlOperation`), so it runs on
  the queue worker, off the caller's thread, and every call made during it queues behind it. The
  switch keeps the queue (a `Reset` replaces it), so those calls run on the new graph. The switch
  never takes `_operationAdmissionGate`: a `Dispose` or `Reset` that arrives during a switch waits
  for its turn behind the switch, then runs normally (since ENG-024 it holds the gate only to
  take its turn, cancels the switch's reopen, and waits with a bound). The same mode
  returns `true` without rebuilding; this is decided when the switch runs, so a second queued
  switch to the same mode is a no-op.
- **Settings carried over.** The switch captures the host's settings from the old graph and
  applies them to the new one before its events are forwarded (`CreateSessionGraph` now applies
  the mode and these settings before subscribing), so values that did not change raise no change
  events. Carried: `PlayOnLoad`, `ResetPitchOnMediaLoad`, `ResetTempoOnMediaLoad`, volume, pitch,
  tempo, normalization, equalizer, `ShowMidiLyrics`, `VideoDisplayMode`, `BaseVisualTransform`,
  CD+G background opacity, and the per-item sync offsets (restored after the reopen, since a load
  clears them). `AutoBackground`, `TransparentBackground`, `VideoFrameTiming`, `ScrubAudio`, the
  prepared-next fade and auto-transition are player-level already. `Reset` still starts from the
  defaults, as before (only its surfaces and host background options survive).
- **Loop.** `MediaControls.HostLoopMedia` records the value the host asked for, also while
  Background mode forces looping on (setting `false` there is still ignored with a Warning, but
  remembered). The new graph gets the host's value, then the mode's forced loop on top. When the
  visible `LoopMedia` differs after the switch, `LoopMediaChanged` is raised once (old, new).
- **Reopen.** `MediaFile` keeps the arguments of the open media (`CurrentLoad`: source, explicit
  background, `ignoreSidecar`); `Load(path, backgroundPaths)` keeps the background it picked. The
  switch reopens it with `MediaFile.Reopen`: no autoplay and no pitch/tempo load reset (same
  item). Then it seeks to the saved position and restores the transport: Playing (and Buffering)
  plays; Paused pauses, and where freshly opened audio cannot pause before playing, it plays muted
  for an instant (the scrub mute) and pauses; Stopped stays stopped. The reopen is a real close
  and load: hosts see `MediaClosed`, `MediaLoaded` (with the new mode's content: in Audio mode a
  video file has `MediaContent.Audio` only) and the state changes, then `ModeChanged`. No
  `SeekCompleted` is raised for the restore seek. There is a short gap, like a seek.
- **Unaccepted source.** The switch does not pre-check the source: it reopens it through the
  normal load, so the new mode's rules apply exactly (including the Audio-mode probe for video
  files without audio). When the reopen fails, the switch still happens and returns `true`, the
  player ends closed, and a `PlayerMode` Warning names the file ("Background mode could not
  reopen song.mp3; the player is closed."), next to the mode's own rejection Warning and
  `UnsupportedFormat` error event. `MediaClosed` and `StateChanged` (to `Null`) were raised by the
  close, as for a normal close. Rejected alternative: closing without trying (needs a file-type
  probe that duplicates the engine's routing, and cannot see a silent video).
- **Prepared-next survives.** The slot is player-level, not part of the graph, so the switch keeps
  it (no `PreparedNextChanged`). Its analysis is plain data and stays valid. An item the new mode
  does not accept fails at transition with the mode's normal rejection, as an item prepared in that
  mode already does (`PrepareNextAsync` never checked the mode). `Reset` still clears it.
- **Surfaces.** The player's `_presentationSurfaces` is the host's surface set, guarded by a new
  `_surfaceGate`. During a rebuild the surfaces are held: the old coordinator is disposed (renderer
  resources freed) and host attach/resize/detach calls only update the set (attach and resize
  return `true`). After the build they are attached to the new coordinator at their latest size; a
  surface it refuses is dropped, as before. `AudioOnlyPresentationCoordinator` now accepts surfaces
  (returns `true`, with an Info diagnostic) and only remembers their ids, so in Audio mode the
  player's set stays current and adapters keep their normal attach/resize/detach lifecycle (with
  `false`, adapters never detach, and a stale handle would be attached on return). This changes the
  slice 1 behaviour (attach returned `false` with a Warning). Open item, closed for Avalonia by
  027A slice 5: a surface kept in Audio mode showed its last video frame; the Avalonia
  `MediaSurface` now shows only its background while the session is in Audio mode (the WPF and
  WinForms adapters are not changed by it). The host's surface calls hold
  `_surfaceGate` only around their own coordinator call; the rebuild holds it only while it
  reattaches, never while the graph is torn down or built.
- **ModeChanged** is queued on the control queue after the switch, so it arrives after the
  events of the reopen; it is raised only when the mode changed.
- **Build failure.** If the new mode's graph cannot be built, the switch logs a `PlayerMode` Error,
  rebuilds the previous mode, reopens the media there and returns `false`. If that rebuild fails
  too, the task faults and the player is left as a failed `Reset` leaves it.
- **Avalonia test player.** Each `PlayerPanel` has a Mode combo box (Full, Background, Audio) that
  calls `SetModeAsync`; `--mode` sets its initial value. The Loop box follows the mode after a
  switch without changing the host's loop value.

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
