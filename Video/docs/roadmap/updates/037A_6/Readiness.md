# 037A slice 6: is BASS ready to retire? (updated 2026-11-10)

Slice 6 is the owner's decision. This note gives the facts for it. The removal itself is prepared on a draft PR
(`StageUpdate.md`): FatzAudio the default, BASS and its API gone, build and tests green; it merges only after the
hand tests and the owner's go-ahead.

## Short answer

**Ready in code; waiting only on the owner.** A player with the FatzAudio engine does everything
without BASS and starts with no BASS libraries present: files and archives (slice 2), loudness normalization, CD+G
(slice 3, classic and scaled), MIDI/KAR with FatzAudio-drawn lyrics, trackers (slice 4), video audio, soundtracks
and network audio on FatzAudio's clock (slice 5, FFmpeg-decoded or, opt-in, FatzAudio-decoded packets), choosing
the output device, and device plug/unplug events. Two things are left, both the owner's:

1. **The hand tests pass**: `powershell -ExecutionPolicy Bypass -File scripts\smoke-037A.ps1` (from `Video\`).
   Part 1 measures by itself; part 2 asks for eyes and ears (sync at tempo 0.75-1.5, seeks, loops, pause/resume, two
   screens at 1080p with no clicks in the first minute, CD+G, lyrics, device choice, a run with no BASS DLLs).
   Part 1 passed on 2026-11-10 on the owner's PC (null device): sync drift at most 1.2 ms over 12 s at any tempo,
   no clicks in seeks, loops, pause/resume or a steady minute.
2. **The decision**: merge the draft PR (`StageUpdate.md`), which makes FatzAudio the only engine and deletes BASS
   (below) in one step, with a minor version bump (public API changes). Nothing BASS is deleted on `main` until then.

Optional switches the owner may want to make the default after hearing them: `--video-audio=fatz` (FatzAudio
decodes soundtracks from packets) and `--cdg-scale` (the scaled, smoothed CD+G picture).

## What was built for the retirement (2026-11-10)

| Item | Where |
|---|---|
| Slices 3-6 | PRs FatzBomb/FatzMedia#10, #11; notes `updates/037A_3` to `037A_5` |
| Output device choice (`AudioOutputDeviceId`, `FatzAudioOutputDevices.Enumerate`, `--audio-device=`) and device events without BASS (one source, each change once) | PR #12, `updates/037A_Devices`; new C call `fa_device_list` in FatzMedia.Audio |
| Hand-test automation and script | PR #13, `updates/037A_5/HandTests.md` |
| A click on every seek of a FatzAudio soundtrack, found by the automation | PR #13: fixed in FatzMedia.Audio's mixer (audio arriving after a push stream ran dry now fades in) |
| MIDI/KAR lyrics drawn by FatzAudio | PR #13, `updates/037A_Lyrics` |
| Scaled CD+G (opt-in) | `updates/037A_CdgScaled` |
| Soundtracks decoded by FatzAudio from packets (opt-in) | `updates/037A_Packets` |

## What deleting BASS would remove (for planning)

- Core: `AudioEngine`, `AudioStreamManager`, `BassPushAudioOutput`, `AudioNormalizer` (BASS FX), `AudioLevelSource`,
  `BassEqualizerFilter`, `BassFadeGainStage`, `BassChannelVolume`, `BassOutputWatchdog`, `BassOutputLatencyProvider`,
  `BassAudioOutputDevices`, `BassMidiEventReader`, `BassMidiSoundfontManager`, `DlsToSf2Converter`,
  `TrackerPositionTimeline`, the BASS branch of `FfmpegAudioFallback`, and the three `ManagedBass` packages.
- Platforms: the BASS loaders and native checks, `PollingAudioDeviceMonitor`, `AudioOutputDevices` (Windows),
  `BassDeviceAudioMonitor` (Linux), `ManagedBass` references.
- Public API that only BASS serves (breaking): `IAudioEngine.PlayHandle`/`SourceHandle`, `ICdgEngine.LoadPaired`
  (already obsolete), the handle overload of `IMidiLyricProvider.ExtractLyrics`, `IAudioOutputDeviceProvider`,
  BASS device indices (`AudioOutputDeviceInfo.DeviceIndex`, `AudioOutputDeviceIndex`), the BASS wording of
  `MediaPlayerModePolicy.AudioUnavailableMessage` and the native dependency report.
- The BASS DLLs in each test player's `native\` folder, and the BASS sections of `docs/NATIVE_DEPENDENCIES.md`.
- Engineering issues that close with it: ENG-028 and ENG-030 (DLS-to-SF2), ENG-040's BASS half, ENG-043's WMA
  crash, and the BASS side of ENG-047.

## Kept either way (separate decisions)

- Our CD+G engine and both lyric renderers (DirectWrite, SkiaSharp) do not use BASS. With FatzAudio they are not
  used any more (FatzAudio draws CD+G and lyrics); deleting them is the other half of the slice 6 decision and does
  not block removing BASS.
- FFmpeg stays: it demuxes every video and soundtrack, and decodes those FatzAudio does not.

## Known limits

- With FatzAudio drawing a song's lyrics, other subtitle cues for that song (a sidecar file next to a .kar) are not
  shown.
- The scaled CD+G and the lyric screen are drawn for a 1920x1080 canvas: crisp up to about 1080p windows.
- A third-party audio engine factory that does not offer the output seam would count as audio being available
  without BASS and stay silent for video (only the FatzAudio factory exists).

## The apps

Kamujo and TriviaBomb do not use BASS (searched 2026-11-10: no ManagedBass reference, no BASS calls, no BASS
DLLs). TriviaBomb plays media with LibVLCSharp today and only mentions in `docs/display-architecture.md` that
FatzAudio replaces BASS. Neither app needs a change for the retirement.
