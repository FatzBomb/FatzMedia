# Stage Update

Stage: 037A.6 - BASS retired: FatzAudio is the only audio engine
Date: 2026-11-10

## Status: implemented on a draft PR, waiting for the owner's hand tests and decision

The change is ready but not merged: it stays a draft until the owner has run `scripts/smoke-037A.ps1` and decided
(`Readiness.md` has the facts). Merging it is a breaking change to the public API (a minor version bump).

## What changed

- **FatzAudio is the default and only built-in engine.** `MediaPlayerConfiguration.AudioEngineFactory` null now
  means `FatzAudioEngineFactory`; a host can still pass its own `IAudioEngineFactory`. When the engine cannot start
  (no `fatzaudio` library, another version, no output) the player warns "Audio engine FatzAudio could not start
  (...). Audio engine unavailable: ..." and plays video without sound; audio media does not open (ENG-033 as before,
  without BASS).
- **The bridge folds into FatzMedia.Video** (owner 2026-11-08): `FatzMedia.Video.Audio` is gone; its code is in
  `FatzMedia.Video/Audio/FatzAudio` (namespace `FatzMedia.Video.Audio` kept), and Core references
  `FatzMedia.Audio` by project. Core has no friend assembly besides tests any more.
- **BASS code removed:** `ManagedBass`, `ManagedBass.Fx` and `ManagedBass.Midi`; the BASS engine and stream manager,
  BASS push output, normalizer and peak scanner, level source, EQ and fade stages, channel volume, output
  watchdog and recovery tracker, latency provider and heuristic, BASS device list and monitors (Windows polling,
  Linux snapshot), MIDI soundfont manager, DLS-to-SF2 converter, BASS MIDI reader, tracker timeline, silence
  analyzer and its analysis cache, the Windows and Linux BASS loaders, and the BASS tests.
- **Public API removed (breaking):** `MediaPlayerConfiguration.AudioOutputDeviceIndex` and
  `EnableUsbBluetoothLatencyHeuristic`; `DLLPath.Bass`; Windows `AudioOutputDevices` (use
  `FatzAudioOutputDevices.Enumerate()`); `AudioOutputDeviceInfo` is now `(string? Id, string Name, bool IsPrimary)`;
  `IPlatformServices.IsAudioAvailable`; `NativeDependencyReport.IsAudioAvailable` and `AudioUnavailableLine`;
  `ICdgEngine.LoadPaired`; `IAudioEngine.PlayHandle`/`SourceHandle`; `IOutputLatencyProvider`;
  `LinuxPlatformOptions.AudioDevicePollInterval`. `IMidiLyricProvider` is internal now (reader-based only).
  `MediaPlayerModePolicy.AudioUnavailableMessage` reads "Audio engine unavailable".
- **File types:** FatzAudio registers the audio extensions it opens (`FatzAudioFileTypes`, from
  `AudioEngine.SupportedFormats`, video containers, CD+G and HLS left out) when the first engine starts; the
  FFmpeg conversion fallback covers the rest.
- **Platforms load FFmpeg only.** Device events come only from the engine (`AudioEngineDeviceMonitor` over
  `IAudioDeviceChangeSource`).
- **Test players:** `--audio=` is gone (FatzAudio always); `--audio-device=`, `--cdg-scale`, `--cdg-effects` and
  `--video-audio=fatz` stay. Their `native\` folders need FFmpeg only.
- **Scripts and docs:** `smoke-037A.ps1` drops `--audio=fatz` and M12 now checks that the player folder holds no
  `bass*.dll` and a song plays; `smoke-backlog.ps1` and `smoke-audit.ps1` check FFmpeg only and the no-library step
  removes `fatzaudio.dll`; `setup-linux-native.sh` no longer downloads BASS; the native manifest lists FFmpeg only.
  `NATIVE_DEPENDENCIES.md`, `CURRENT_ARCHITECTURE.md`, the READMEs, `AGENTS.md` and the site articles describe
  FatzAudio.
- **Kept:** our `CdgEngine` and both lyric renderers (the fallback for an engine without the CD+G and lyric seams),
  `IAudioEngine.Init` (hosts), FFmpeg for every video and soundtrack.

## Decisions

- One commit for the code, one for the docs: the removal only compiles as a whole.
- Unused code found by the removal was deleted with it (each proven unreferenced outside tests): the analysis
  cache and key, the polling loop, the silence-region detector, the filter-chain planner, the latency heuristic.
- `IMediaFileAnalyzer` (prepared-next analysis) has no implementation now, as it had none with FatzAudio before:
  `AnalyzeAsync` returns no analysis. Left for a follow-up (ENG-062) rather than reworking prepared-next here.
- The startup dependency report (`NativeDependencies`) lists FFmpeg only; a missing `fatzaudio` shows as the
  Bootstrap warning, not in the report. Adding it to both platforms' reports is a small follow-up if hosts want it.
- The pre-PR review (`/code-review medium --fix`) fixed a broken error filter in `smoke-backlog.ps1`, an engine
  leak if registering file types failed, a dispose race in `AudioEngineDeviceMonitor`, a stale project in the docfx
  config, and moved the test players' audio options into one shared helper.

## Verification (Windows, this machine)

Release build 0 warnings (`FatzMedia.Video.sln` and the root `FatzMedia.sln`); tests: Video 1514, Portable 1684,
Linux 200 (FatzAudio native tests ran with this machine's `fatzaudio.dll`; the other Linux native tests return at
once on Windows), Vulkan 103, Avalonia 52, all passed; the four boundary scripts and
`dotnet format --verify-no-changes` pass. Not verified here: a real audio device, A/V sync by eye, clicks by ear,
Linux CI.

## Manual smoke (owner)

1. Build in Release (`Audio` first if it changed: `Audio\docs\building.md`).
2. Run `powershell -ExecutionPolicy Bypass -File scripts\smoke-037A.ps1` from `Video\` (no `--audio=` needed any
   more). M12 lists any `bass*.dll` still in the player folders: delete them, they are not used.
3. Start each test player without options and play an MP3, a .kar, an MP3+G, a video and a tracker: all play, the
   Log says `Audio engine: FatzAudio`.
4. Rename `fatzaudio.dll` in a copy of the WPF player folder and start it: it starts, a warning says the audio engine
   could not start, a video plays its picture, an MP3 is refused with an error.
5. If all is well, mark the draft PR ready and merge it.
