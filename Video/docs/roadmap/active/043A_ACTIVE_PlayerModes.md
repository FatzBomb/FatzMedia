# Active Stage 043A - Player Modes (Full, Background, Audio)

Prepared: 2026-10-23
Status: slice 1 in progress.
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
