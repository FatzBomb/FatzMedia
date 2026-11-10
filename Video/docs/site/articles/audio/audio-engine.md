# Audio engine: FatzAudio

The player's audio engine is FatzAudio. It plays audio files and archive entries, MIDI and KAR
(with lyrics), tracker modules, CD+G audio, the soundtracks of videos and network audio. There is
nothing to choose or switch on: `MediaPlayerConfiguration.AudioEngineFactory` at `null` (the
default) uses it. BASS was removed in 037A.

## Setting it explicitly

You can set the factory yourself to make the choice visible in code:

```csharp
using FatzMedia.Video.Audio;

var configuration = new MediaPlayerConfiguration
{
    AudioEngineFactory = new FatzAudioEngineFactory(),
};
```

<xref:FatzMedia.Video.Core.Configuration.MediaPlayerConfiguration.AudioEngineFactory> is read when
the player is created; to change it, call `Reset` with a new configuration.

If FatzAudio cannot start (its native library is missing, or is another version than the
binding), the player raises a warning diagnostic in category `Bootstrap`
("Audio engine FatzAudio could not start ... Audio engine unavailable") and plays video without
sound. Audio media does not open.

## What FatzAudio does

| Feature | Behavior |
|---|---|
| Position | FatzAudio's presentation clock: what is heard now, device latency included |
| Loop | Gapless; `MediaLooped` is raised once per wrap |
| Equalizer | FatzAudio's built-in equalizer, one peaking band per band (one octave is Q 1.414) |
| Fades and prepared-next transitions | FatzAudio volume ramps |
| Normalization | Loudness (LUFS) normalization with a true-peak limiter: every song at `MediaPlayerConfiguration.Normalization.TargetLufs` (default -14 LUFS, peaks at -1 dBTP). A song is scanned once and remembered in a per-user cache folder; a first play starts on an estimate and glides to the measured level within a fraction of a second. See [Normalization](normalization.md) |
| Silence trimming | FatzAudio finds the silence when the file opens, so the trim applies from the first play; it measures RMS over 50 ms windows at your threshold, and sounds shorter than 200 ms at the edges (clicks, count-in ticks) count as silence |
| Output device | The system default device, which FatzAudio follows, or the device named by `AudioOutputDeviceId`. See [Audio output devices](output-devices.md) |
| MIDI | `MidiSoundfontPath` (SF2, SF3 or DLS) or the system bank (Windows `gm.dls`); lyrics and karaoke metadata |
| Formats FatzAudio cannot decode | Converted by FFmpeg |
| Network audio | Opens through FFmpeg, for `http`, `https` and HLS |

## The engine and its native library

`FatzMedia.Audio` comes from `Audio\` in the FatzMedia repository, by project reference. Its native
library (`fatzaudio.dll` on Windows, `libfatzaudio.so` on Linux) is built from the same folder with
CMake and copied next to the app by the build; there is nothing to copy into `native/`. See
`docs/NATIVE_DEPENDENCIES.md` in the repository. End users need no audio licence or audio DLLs
beyond that library.
