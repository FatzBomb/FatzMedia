# FatzMedia.Video.Audio

Opt-in [FatzAudio](https://github.com/FatzBomb75/fatzaudio) audio engine for
FatzMedia.Video (stage 037A, slice 2). It plays audio files and audio entries of archives
through FatzAudio instead of BASS. BASS stays the default: nothing changes unless a player is
created with this engine.

```csharp
using FatzMedia.Video.Audio;

var configuration = new MediaPlayerConfiguration
{
    AudioEngineFactory = new FatzAudioEngineFactory(),
};
```

The test players take `--audio=fatz` (and `--audio=bass`, the default).

## What it covers

- Open (file path or archive entry), play, pause, stop, seek, duration, volume, pitch, tempo.
- Position from FatzAudio's presentation clock (`fa_stream_get_clock`, device latency included).
- Loop on and off while playing, with one `PlaybackEnded` per wrap while still playing, as the
  BASS engine raises it.
- Equalizer: FatzAudio's built-in bands, one peaking band per `AudioEqualizerBand` (one octave is
  Q 1.414).
- Fades (`FadeInAsync`, `FadeOutAsync`, prepared-next transitions): FatzAudio volume ramps on the
  stream.
- Normalization: FatzAudio's loudness (LUFS) normalization with its true-peak limiter, set by
  `MediaPlayerConfiguration.Normalization` (default -14 LUFS, -1 dBTP). Each song's loudness comes
  from ReplayGain/R128 tags, FatzAudio's scan cache (a per-user folder by default) or a scan on all
  cores; a first play starts on FatzAudio's running estimate and glides to the measured level.
- Silence trim: FatzAudio's silence detection at open, so it applies from the first play; the
  trimmed window is the stream's cue-in and cue-out.
- The default output device, followed by FatzAudio; its device moves raise
  `AudioDeviceChanged`.
- MIDI (`.mid`, `.kar`, `.rmi`) with the configured `MidiSoundfontPath` (SF2, SF3 or DLS) or the
  system bank, with lyrics and karaoke metadata read from FatzAudio's MIDI texts and marks.
- Formats FatzAudio cannot decode (for example WMA off Windows) are converted by FFmpeg, as for
  BASS.
- Tracker modules play through FatzAudio (libopenmpt). FatzAudio does not follow a module's jump
  back to its restart order, so a module's length is fixed and silence trim is not applied to
  modules (as on BASS).

CD+G is drawn by FatzAudio (037A slice 3). Video audio, video soundtracks (Audio mode) and network
audio through FFmpeg play through FatzAudio too (slice 5): FFmpeg decodes, FatzAudio plays, and the
picture follows FatzAudio's clock. A player with this engine uses no BASS and needs no BASS
libraries. If the engine cannot start (for example the FatzAudio native library is missing), the
player warns and uses BASS when BASS is there, otherwise it has no audio.

## Dependencies

- `FatzMedia.Audio`, the .NET binding in `Audio\bindings\dotnet` of this repo, by project reference.
  Its native library comes from Audio's CMake build (`docs/NATIVE_DEPENDENCIES.md`, "Building the
  native library"); the binding refuses a native library of another major.minor version.

FatzAudio's own code is proprietary (all rights reserved); its third-party components keep their
licences. See the package's `LICENSE`.
