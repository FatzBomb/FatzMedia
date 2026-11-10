# 037A slice 6: is BASS ready to retire? (2026-11-10)

Slice 6 is a decision, not a build. This note gives the facts for it after slices 3-5.

## Short answer

**Not yet, but close.** In code, a player with the FatzAudio engine (`--audio=fatz`) now does everything without
BASS: files and archives (slice 2), loudness normalization, CD+G (slice 3), MIDI and trackers (slice 4), video
audio, soundtracks and network audio (slice 5), and it starts with no BASS libraries present. What is left is
proof on real hardware, two missing features, one owner decision, and then the deletion itself.

## What is left, in order

1. **Owner hand tests pass** (`updates/037A_5/HandTests.md`; also the pending normalization smoke,
   `updates/037A_Normalization`). Sync at tempo 0.75-1.5, seeks, loops, pause/resume and the two-screen 1080p
   first minute are only judged on the owner's machine.
2. **Choose an output device on FatzAudio.** Today the FatzAudio engine always plays on the system default and
   follows it; `AudioOutputDeviceIndex` is a BASS index and is ignored with a warning, and the hosts' device list
   (`FatzMedia.Video.Windows` `AudioOutputDevices`) comes from BASS. A karaoke host needs to pick the PA output.
   Needed: a device id on `MediaPlayerConfiguration` mapped to FatzAudio's `OutputDevices`, and a device list API
   that is not BASS's.
3. **Device-list change events without BASS.** The platform monitors (`PollingAudioDeviceMonitor` on Windows,
   `BassDeviceAudioMonitor` on Linux) poll BASS. Without BASS only the FatzAudio engine's own events remain
   (its device moved or went away), not "a device was added". Needed if a host refreshes its device picker live.
   (With BASS present, one change is still reported twice: slice 2 note.)
4. **Owner decision: make FatzAudio the default.** Flip `MediaPlayerConfiguration.AudioEngineFactory`'s default
   (or the test players' `--audio` default) and live with it for a while, with BASS as the fallback.
5. **Then delete BASS** in one PR, with a minor version bump (public API changes). Not done here: nothing BASS was
   deleted.

## What deleting BASS would remove (for planning)

- Core: `AudioEngine`, `AudioStreamManager`, `BassPushAudioOutput`, `AudioNormalizer` (BASS FX), `AudioLevelSource`,
  `BassEqualizerFilter`, `BassFadeGainStage`, `BassChannelVolume`, `BassOutputWatchdog`, `BassOutputLatencyProvider`,
  `BassAudioOutputDevices`, `BassMidiEventReader`, `BassMidiSoundfontManager`, `DlsToSf2Converter`,
  `TrackerPositionTimeline`, the BASS branch of `FfmpegAudioFallback`, and the three `ManagedBass` packages.
- Platforms: the BASS loaders and native checks, `PollingAudioDeviceMonitor`, `AudioOutputDevices` (Windows),
  `BassDeviceAudioMonitor` (Linux), `ManagedBass` references.
- Public API that only BASS serves (breaking): `IAudioEngine.PlayHandle`/`SourceHandle`, `ICdgEngine.LoadPaired`
  (already obsolete), the handle overload of `IMidiLyricProvider.ExtractLyrics`, `IAudioOutputDeviceProvider`,
  BASS device indices in `AudioOutputDeviceInfo` and `AudioOutputDeviceIndex`, the BASS wording of
  `MediaPlayerModePolicy.AudioUnavailableMessage` and the native dependency report.
- The BASS DLLs in each test player's `native\` folder, and the BASS sections of `docs/NATIVE_DEPENDENCIES.md`.
- Engineering issues that close with it: ENG-028 and ENG-030 (DLS-to-SF2), ENG-040's BASS half (trim from the
  second play), ENG-043's WMA crash, and the BASS side of ENG-047.

## Kept either way (separate decisions)

- Our CD+G engine and both lyric renderers (DirectWrite, SkiaSharp) do not use BASS. FatzAudio draws CD+G now; our
  lyric renderers still draw MIDI lyrics on both engines. Whether to delete our CD+G engine and move lyrics to
  FatzAudio's `LyricSurface` is the other half of the slice 6 decision; it does not block removing BASS.
- FFmpeg stays: it demuxes and decodes every video and soundtrack.

## The apps

Kamujo and TriviaBomb do not use BASS (searched 2026-11-10: no ManagedBass reference, no BASS calls, no BASS
DLLs). TriviaBomb plays media with LibVLCSharp today and only mentions in `docs/display-architecture.md` that
FatzAudio replaces BASS. Neither app needs a change for the retirement.
