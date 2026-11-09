# Audio engine: BASS or FatzAudio

Audio files and audio entries of archives play through BASS by default. FatzAudio, a newer engine
that will replace BASS once it is proven, can play them instead: it is opt-in, one player at a
time, and everything else stays as it is.

## Choosing FatzAudio

Reference the `FatzMedia.Video.Audio` package and set
<xref:FatzMedia.Video.Core.Configuration.MediaPlayerConfiguration.AudioEngineFactory> to its
factory when you create the player:

```csharp
using FatzMedia.Video.Audio;

var configuration = new MediaPlayerConfiguration
{
    AudioEngineFactory = new FatzAudioEngineFactory(),
};
```

Leave `AudioEngineFactory` at `null` (the default) for BASS. The choice is made when the player is
created; to change it, call `Reset` with a new configuration. The test players take
`--audio=fatz` (and `--audio=bass`, the default).

If FatzAudio cannot start (its native library is missing, or is another version than the
package's), the player raises a warning diagnostic in category `Bootstrap` and uses BASS.

## What changes with FatzAudio

The player's API is the same with either engine. With FatzAudio:

| Feature | With FatzAudio |
|---|---|
| Position | FatzAudio's presentation clock: what is heard now, device latency included |
| Loop | Gapless; `MediaLooped` is raised once per wrap, as with BASS |
| Equalizer | FatzAudio's built-in equalizer, one peaking band per band (one octave is Q 1.414) |
| Fades and prepared-next transitions | FatzAudio volume ramps |
| Normalization | Loudness (LUFS) normalization with a true-peak limiter: every song at `MediaPlayerConfiguration.Normalization.TargetLufs` (default -14 LUFS, peaks at -1 dBTP). A song is scanned once and remembered in a per-user cache folder; a first play starts on an estimate and glides to the measured level within a fraction of a second. See [Normalization](normalization.md) |
| Silence trimming | FatzAudio finds the silence when the file opens, so the trim applies from the first play, not only from the second; it measures RMS over 50 ms windows at your threshold, and sounds shorter than 200 ms at the edges (clicks, count-in ticks) count as silence |
| Output device | Always the system default device, which FatzAudio follows; a device move raises `AudioDeviceChanged`. `AudioOutputDeviceIndex` (a BASS index) is not used and logs a warning |
| MIDI | `MidiSoundfontPath` (SF2, SF3 or DLS) or the system bank (Windows `gm.dls`); lyrics and karaoke metadata as with BASS |
| Formats FatzAudio cannot decode | Converted by FFmpeg, as with BASS |
| Plain http network audio | Opens through FFmpeg, like https |

Video audio, the soundtrack of a video in Audio mode and network audio still play through BASS,
so the BASS libraries are still needed and BASS is still initialized on the configured device.

## The engine and its native library

`FatzMedia.Audio` comes from `Audio\` in the FatzMedia repository, by project reference. Its native
library is built from the same folder with CMake and copied next to the app by the build; there is
nothing to copy into `native/`. See `docs/NATIVE_DEPENDENCIES.md` in the repository.
