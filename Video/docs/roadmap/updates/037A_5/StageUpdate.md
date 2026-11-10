# Stage Update

Stage: 037A.5 - Video audio on FatzAudio, and a FatzAudio player that needs no BASS
Date: 2026-11-10

## Status: implemented, pending manual smoke (timing is judged by ear and eye on the owner's machine)

With `--audio=fatz` (`MediaPlayerConfiguration.AudioEngineFactory = new FatzAudioEngineFactory()`), video audio,
Audio-mode soundtracks and network audio through FFmpeg (http, https, HLS) play through FatzAudio, and the
picture follows FatzAudio's presentation clock. Together with slices 2-4, a FatzAudio player no longer uses BASS
for anything: it starts and plays with no BASS libraries present. Without the factory (the default) nothing
changes: BASS plays as before, through the same code moved into its own class.

## Design

FFmpeg keeps demuxing and decoding every soundtrack; only plain float PCM crosses into the audio library (the
slice 5 rule: no FFmpeg object crosses).

| Part | Where | What |
|---|---|---|
| `IPushAudioOutput` (internal) | Core, `Core/Audio` | Where `VideoAudioEngine`'s decoded PCM plays: create, push, play, pause, restart (seek/stop), position, volume, pitch, tempo, EQ, fades, normalization. |
| `BassPushAudioOutput` | Core | The BASS push stream + BASS_FX tempo stream, our peak normalization, fades, EQ and volume: the engine's BASS code moved out unchanged. |
| `VideoAudioEngine` | Core | Keeps everything FFmpeg: demux, decode, resample, seek trim, gapless loop wraps, the push throttle and the position bookkeeping. 1,293 lines to 1,010; no BASS call left in it. |
| `IPushAudioOutputSource` (internal) | Core | An audio engine whose device plays soundtracks. `VideoPipelineFactory` and `PlaybackCoordinator` take outputs from it; otherwise BASS on the player's device. |
| `FatzAudioPushAudioOutput` | `FatzMedia.Video.Audio` | A FatzAudio push stream on its own bus of the player's FatzAudio device. |
| `PlayerServiceProvider` | Core | Creates the factory's engine first; BASS is created only without a factory or when the factory fails. Audio is available when either works. |

Decisions:

- **The clock.** Each push is stamped with its place on the engine's continuous timeline (the first push after a
  restart and any jump; contiguous audio is not re-stamped), so FatzAudio's clock (what is heard now, device
  latency included, tempo applied, held while paused) is exactly the position the video side already expects,
  across loop wraps too. After a seek or stop FatzAudio's clock holds the old time until new audio is queued, so
  the position is the restart target until then. No BASS latency estimate (`IOutputLatencyProvider`) is applied
  on FatzAudio: its clock includes the device buffer.
- **Push streams only, no packet streams.** FFmpeg already decodes every soundtrack here; one path is simpler to
  keep right. Packet streams (FatzAudio decoding MP3, AAC, AC-3 itself) are an option for later if CPU matters.
- **Normalization.** FatzAudio's loudness, as for files: the stream names its song
  (`StreamOptions.SourceIdentity`, the file's fingerprint), so FatzAudio's cache applies; a local file of up to
  15 minutes is scanned by path in the background (a 4-minute music video takes a fraction of a second) and the
  result is handed to the playing stream (`SetLoudness`; a scan by path does not reach a push stream by itself).
  Longer files (films) keep FatzAudio's running estimate and glide; no second read of a film (ENG-052's cost does
  not arise). Network sources use the running estimate.
- **Device.** The outputs play on the player's FatzAudio device, which follows the default device. Selecting an
  explicit output device for FatzAudio is not built: `AudioOutputDeviceIndex` is a BASS index (a warning says so).
- **Device events.** With a factory engine the player's monitor is the engine's own events, plus the platform's
  BASS device-list monitor only when BASS loaded (it is registered only then). With BASS present, one change can
  still be reported twice (slice 2 note); without BASS, once.
- **Volume, pitch, tempo, EQ, fades:** as on the file engine (bus volume; stream volume ramps for fades; FatzAudio's
  16-band EQ; pitch in semitones, FatzAudio clamps to +-36; tempo 0.1-10).

## Tests

- `FatzAudioVideoAudioNativeTests` (8, real FFmpeg and FatzAudio on the null device, real time; Windows here,
  Linux CI with `FATZBOMB_LINUX_FFMPEG_DIR`): the position follows FatzAudio's clock in real time (0.85-1.15 x),
  tempo 0.75 and 1.5 scale it (within 12 %), a seek while playing starts at the target and goes on, pause holds the
  clock, a seek while paused shows the target and play resumes from it, a 1 s loop wraps gaplessly with one count
  per wrap and the position never leaves 0..1, stop goes back to 0 and plays again, and normalization takes the
  scanned loudness (a 0.1 sine, -20.7 LUFS, gets x2.16 at -14). Passed four runs in a row.
- `AudioEngineSelectionTests` (+2): a factory engine gives audio when BASS did not load; a failing factory without
  BASS warns "no audio" and uses the silent engine.

## Verification (Windows, this machine)

See "Verification" in the PR. Not verified here: a real audio device, A/V sync by eye, clicks by ear, a run with
the BASS DLLs removed (the test above covers the service graph only).

## Manual smoke (owner)

The hand-test checklist for slices 3-5 is `updates/037A_5/HandTests.md`.
