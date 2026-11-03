# Stage Update

Stage: 037A.2 - FatzAudioEngine for files and archives (opt-in; BASS stays the default)
Date: 2026-11-03

## Status: slice 2 implemented, pending manual smoke

A new package, `FatzBomb.MediaControl.Audio.FatzAudio`, plays audio files and archive entries
through FatzAudio `1.0.0-rc.1` when a player is created with its factory
(`MediaPlayerConfiguration.AudioEngineFactory`; the test players' `--audio=fatz`). With no
factory, the default, nothing changes: the same BASS engine and service graph as before. Video
audio, video soundtracks (Audio mode) and network audio stay on BASS (slice 5). The FatzAudio
paths ran here on Linux against FatzAudio's real native library on its null output device; Windows,
WASAPI and any real device are the owner's smoke below.

## Design

| Part | Where | What |
|---|---|---|
| `IAudioEngineFactory` (public) | Core, `Core/Abstractions` | `Name`, `Create(MediaPlayerConfiguration)`. The only new public API in Core, with `MediaPlayerConfiguration.AudioEngineFactory` (`null` = BASS). |
| Selection | `MediaControlServiceProvider.CreateAudioEngine` | Creates the BASS engine as before (it initializes BASS on the configured device, which video audio needs), then, when a factory is set, calls it, disposes the BASS file engine (BASS stays initialized) and uses the factory's engine. A throwing factory gives a `Bootstrap` warning and BASS. Audio availability is still BASS's: without BASS there is no audio with either engine. |
| Device changes | `AudioEngineDeviceMonitor` (Core, internal) | With a factory, the player's `IAudioDeviceMonitor` forwards the engine's own moves (`IAudioDeviceChangeSource`, internal) and the platform's BASS monitor (still watching video audio). |
| `FatzAudioEngineFactory` (public) | new package | The opt-in. An internal constructor plays to FatzAudio's null device (tests). |
| `FatzAudioEngine` (internal) | new package | `IAudioEngine` plus the slice-1 seams `IAudioLoopControl`, `IAudioAnalysisOpenControl`, `IAudioTrimWindowProvider`, `IAudioFadeGainTarget`, `IAudioFilterHost`, `IAudioVolumeSlide`, `IMidiEventReaderSource`, and `IAudioDeviceChangeSource`. One FatzAudio engine, default output device and mix bus per player. |
| `FatzAudioMidiEventReader` | new package | `IMidiEventReader` over FatzAudio's MIDI texts and marks (a snapshot; positions are 100 ns ticks). |
| `FatzAudioLevelSource` | new package | `IAudioLevelSource` over a FatzAudio decode-only stream: the sample peak of what `fa_stream_read` returns. |
| `FatzAudioMappings` | new package | The pure mappings below. |
| `--audio=bass|fatz` | `PlayerStartupOptions.AudioEngine` (Player.Shared) | Each host's engine factory (WPF, WinForms, Avalonia) sets `FatzAudioEngineFactory` for `fatz`; unknown values warn and use BASS. The status line shows `Audio engine=`. |

The engine package implements Core's internal seams, so Core gets one non-test
`InternalsVisibleTo` (`FatzBomb.MediaControl.Audio.FatzAudio`); the packages version in lockstep.
`check-core-boundary.ps1` was updated on purpose: the new project in the DAG (Core only), the
hosts and `Linux.Tests` referencing it, the friend-assembly exception, Core never referencing the
`FatzAudio` package or namespace, and the package pinned to exactly one version (`[x.y.z]`). The
adapter, WPF and parity scripts needed no change.

## Mapping decisions

- **Position:** the stream's presentation clock (`fa_stream_get_clock`, `AudioStream.Clock`): what is
  heard now, device latency included, as the BASS engine's latency-corrected position. Seeks use
  the blocking `Position` setter, as the BASS engine's seek is synchronous (and a stopped stream
  then plays from there).
- **Volume, fades, normalization (gain stages):** the stream's volume is the fade stage
  (`IAudioFadeGainTarget`, FatzAudio linear volume ramps; a new session per open, stop, natural
  end and loop wrap restores 1, like `BassFadeGainStage`). The player's mix bus carries user
  volume times the normalization gain (`IAudioVolumeSlide` ramps the bus). The EQ runs before both.
- **Normalization: our peak rule, not FatzAudio's loudness.** `Normalize`, `NormalizationGain`,
  `IsNormalizationReady` and `NormalizationGainChanged` keep their meaning: the quick pass (may
  only attenuate) then the deep pass (`NormalizationGainPolicy`), gain 0.8 / peak up to 8x, the same
  `NormalizationGainCache` keys as BASS. The peak is measured by the existing `AudioNormalizer`
  on a FatzAudio decode-only stream of the same file or archive bytes. Reasons: the public contract
  is a peak gain multiplier with two passes, which FatzAudio's LUFS target does not map onto; A/B
  against BASS compares like with like (the same gain for the same file); and an engine switch
  does not change the level of a library the owner already knows. FatzAudio's normalization is set
  to `Off` and every stream opens with `NoNormalize`. Moving to LUFS is a separate decision (it
  changes levels), noted for slice 6.
- **Silence trim from the first play.** At open (plain files, trim on, not tracker modules, as on
  BASS) the engine calls `AudioStream.DetectSilence`, which decodes only the edges. Settings: RMS
  over 50 ms windows (FatzAudio's default, what we asked for in request 9), our
  `ThresholdDecibels` (-50 dBFS default), our `MinimumSilenceDuration` (0.5 s default), and sounds
  shorter than 200 ms count as silence (`MinSound`, fixed in this slice; we said we would expose it,
  which is new public API, so it waits for an owner decision). The first audible frame and the frame
  after the last audible one become edge regions for our `SilenceTrimCalculator`, which applies our
  padding (0.1 s) and the leading/trailing switches, so the window has the same shape as BASS's. The
  window becomes the stream's CUE_IN/CUE_OUT: play from stopped or ended starts at the trimmed start,
  the stream ends exactly at the trimmed end (`Ended`), and a loop wraps CUE_OUT to CUE_IN, all
  natively. `AudioPipeline` maps positions through `IAudioTrimWindowProvider` as before. ENG-040 (trim
  only from the second play) is therefore fixed for the FatzAudio engine; BASS is unchanged. The
  `MediaAnalysisCache` is not used (detection takes milliseconds). Note: RMS at -50 dBFS treats a
  little more as silence than BASS's per-window peak at the same threshold.
- **Loop:** `AudioStream.Loop`, settable while playing. Each `Looped` event raises
  `PositionChanged` (the wrap target) and `PlaybackEnded` while the state stays `Playing`, exactly as
  the BASS engine re-arms its end sync per wrap, so the player raises one `MediaLooped` per wrap.
  `Ended` raises `PlaybackEnded` with `Stopped`.
- **Equalizer:** FatzAudio's built-in EQ (`SetEqualizer`), one peaking band per
  `AudioEqualizerBand`: centre, gain, and Q from the bandwidth in octaves (RBJ: sqrt(2^N) / (2^N - 1),
  so one octave is Q 1.414, the same shape as BASS_FX's PEAKEQ with that bandwidth). Disabled or flat
  is an empty band list (FatzAudio's EQ off). Changes glide over about 10 ms.
- **Pitch and tempo:** `Pitch` in semitones (FatzAudio clamps to +-36; BASS took +-60), `Tempo` as
  `1 + percent / 100` (FatzAudio clamps to 0.1-10; BASS took 0.05-51).
- **Device:** the default device (`OpenOutput()`), which FatzAudio follows and reopens when it goes
  away. Its `DeviceChanged` (value 0: moved to the new default; 1: the device went away) becomes
  `AudioDeviceChanged` (`DefaultDeviceChanged` / `DeviceRemoved`) with an Info diagnostic.
  `AudioOutputDeviceIndex` is a BASS device index; it is not mapped (a warning says so). On Linux,
  PulseAudio and PipeWire move streams without telling FatzAudio, so there is no event there.
- **MIDI:** `MidiSoundfontPath` is loaded with `SoundFont.FromFile` (SF2, SF3, DLS) as the
  engine's default soundfont; without it FatzAudio uses the system bank (Windows `gm.dls`). No
  bank: the MIDI file does not open (an error diagnostic). Lyrics and metadata:
  `FatzAudioMidiEventReader` gives `Lyric` (FatzAudio's lyric texts, which include a .kar's words),
  `Text`, `TrackName`, `Copyright`, key signatures as `"sharps mode"` and time signatures as
  `"n/d 24 8"` (the builder reads the first word), and tempo marks. FatzAudio strips the Soft
  Karaoke marks into flags; the reader puts `\` (new page) and `/` (new line) back, so
  `MidiLyricCueBuilder` and `MidiMetadataExtractor` run unchanged. Only as much MIDI as a .kar with
  lyrics needs: no soundfont switching, transpose or MIDI effects controls (slice 4).
- **Archives:** the entry is read into memory once (`AudioStreamManager.ReadArchiveEntry`) and
  opened with `OpenMemory`; normalization reopens the same bytes decode-only. Not trimmed (as BASS).
- **Formats FatzAudio cannot decode** (`UnsupportedFormat` on an extension FFmpeg handles): the
  existing `FfmpegAudioFallback` converts to a float WAV, played from memory or its temp file.
  Archive entries are not converted (FatzAudio decodes what karaoke archives hold).
- **Network audio:** the FatzAudio engine is not an `INetworkAudioSource`, so plain http audio now
  opens through FFmpeg into the BASS soundtrack engine, like https and HLS
  (`PlaybackCoordinator.CanOpenNetworkAudio`, true for the BASS engine, so BASS is unchanged).
- **Not covered here:** tracker order/row features and network playback in FatzAudio (slice 4), video
  audio (slice 5), karaoke surfaces (slice 3). Tracker modules do play (FatzAudio does not follow a
  module's jump back to its restart order, so their length is fixed). The engine is not an
  `IMediaFileAnalyzer`, so prepared-next analysis is skipped with FatzAudio (nothing to warm: trim is
  detected at open; normalization still runs its passes at open). `PlayHandle`/`SourceHandle` are 0.
  FatzAudio's `CacheDirectory` is not set (no persistent analysis cache).

## Package access

- `nuget.config` (new, repository root): nuget.org for everything, GitHub Packages
  (`https://nuget.pkg.github.com/FatzBomb75/index.json`) for `FatzAudio` only
  (`packageSourceMapping`), credentials user `FatzBomb75` and password `%FATZAUDIO_PACKAGES_TOKEN%`.
  `auditSources` is nuget.org only: without it NuGet audit queries every source, and a token-less
  restore of any project failed with NU1900 as an error in Release (found here).
- `ci.yml`: `permissions: packages: read` and `FATZAUDIO_PACKAGES_TOKEN: ${{ secrets.GITHUB_TOKEN }}`
  at workflow level (both jobs restore projects that use FatzAudio). `windows-native-smoke.yml` packs
  only Core and the Windows packages, which do not use FatzAudio: unchanged.
- Developers: a classic PAT with `read:packages` in `FATZAUDIO_PACKAGES_TOKEN`
  (`docs/NATIVE_DEPENDENCIES.md`, the package README).
- **Not verified here:** this sandbox has no token, and its proxy refuses GitHub Packages (403). The
  committed `nuget.config` was checked to parse and to send `FatzAudio`, and only `FatzAudio`, to the
  GitHub feed (a restore with an empty package cache failed only on that source). For the builds and
  tests below, FatzAudio was built from source at 59741a5 (the rc.1 commit) in a separate clone
  (`/root/fa-rc1`, `cmake -G Ninja -DCMAKE_BUILD_TYPE=Release`, `ninja fatzaudio`;
  `libfatzaudio.so.1.0.0`, SONAME `libfatzaudio.so.1`, built on Ubuntu 24.04 so it needs glibc 2.38,
  not the package's 2.35), copied to `bindings/dotnet/src/FatzAudio/runtimes/linux-x64/native/`, and
  packed with `dotnet pack bindings/dotnet/src/FatzAudio -c Release -p:VersionSuffix=rc.1 -o /root/fa-rc1-feed`
  as exactly `FatzAudio.1.0.0-rc.1.nupkg` (linux-x64 only). The solution was restored once with
  `dotnet restore FatzBomb.MediaControl.sln --configfile <a nuget.config outside the repository mapping FatzAudio to that folder>`;
  nothing of that is committed. The first CI run is the real check of the feed and of the token's
  package access.

## Tests

New (Linux, against the real FatzAudio library, null output device, real time):
`FatzAudioEngineNativeTests` (9): open/play/seek/pause/stop with a monotonic clock; loop with one
`PlaybackEnded` per wrap while playing, then a single end when turned off; silence trim on the first
open (a 50 ms click inside the leading silence is trimmed, our padding kept, CUE_IN set, the end at the
trimmed end through `AudioPipeline`); the EQ cutting a 1 kHz tone by more than 10 dB (FatzAudio's own
meter); fades through `AudioFadeRunner` with a stale session rejected after Stop; normalization
quick 1.0 then deep 2.0 for a 0.4 peak, on the bus, off with `Normalize = false`, cached on reopen; a
generated .kar (with a generated one-instrument SF2) whose lyrics, tempo and metadata come through
`IMidiEventReader` into `MidiLyricCueBuilder` and which plays; an archive entry from memory; the
service provider using the factory's engine and `AudioEngineDeviceMonitor`.
`FatzAudioMappingsTests` (16): Q, EQ bands, tempo, silence options, edge regions, device-change
kinds, Soft Karaoke marks. Portable: `AudioEngineSelectionTests` (9): BASS by default, the factory's
engine and the combined device monitor, the fallback warning, `--audio=` parsing.

Results: see "Verification on Linux" below.

## Verification on Linux

- `dotnet build FatzBomb.MediaControl.sln -c Release -p:EnableWindowsTargeting=true` (after the local
  restore above): 0 warnings, 0 errors.
- Tests: Portable 1752 (was 1743; +9) passed; Linux 135 (was 110; +25) passed (with `FATZBOMB_LINUX_FFMPEG_DIR`);
  Vulkan 102 passed; Avalonia 47 passed. The 25 FatzAudio tests passed nine
  runs in a row.
- `dotnet format --verify-no-changes`, the four boundary scripts and `scripts/build-docs.sh`: pass.
- Not run here: anything Windows (WPF, WASAPI, the win-x64 native in the real package), a real
  device or device change, BASS (absent here, so no A/B), the GitHub Packages restore.

## Manual Windows smoke (owner)

Setup: set `FATZAUDIO_PACKAGES_TOKEN` (classic PAT, `read:packages`), `dotnet restore` and build the
solution in Release, with the BASS and FFmpeg DLLs in `FatzBomb.MediaControl.Player`'s `native/`
folder as today. Start the WPF player once with `--audio=fatz` and once without (BASS) for each step
and compare on the same files. With `--audio=fatz` the status line shows `Audio engine=FatzAudio`, and
the Log shows `FatzAudio 1.0.0 ready on the default output device` and `Audio engine: FatzAudio (video
audio stays on BASS)`.

1. **Plain files** (MP3, FLAC, OGG, WAV, M4A): play, pause, resume, seek by dragging and clicking,
   stop, play again. The position bar moves smoothly and matches what you hear; the duration matches
   BASS.
2. **Volume, pitch, tempo:** volume slider (no steps); pitch +-3; tempo 75% and 125% (pitch kept).
   Sound quality against BASS.
3. **Loop:** loop on with a short file: gapless, one "looped" per wrap in the Log/events; turn loop
   off mid-play: it ends once.
4. **EQ:** on, move bands while playing (no clicks), a preset, off. Retry the 31 Hz band on a
   bass-heavy track (slice 1 finding): it should be audible here.
5. **Fades:** fade-out and fade-in buttons; `--prepare-next=<file> --fade-next=3 --auto-next`: the
   fade-through sounds as on BASS; the next track does not start muted.
6. **Normalization:** with Normalize on, a quiet track: Log "Quick normalization complete" then "Deep
   normalization complete, gain=..." with the same gain as BASS shows for the file; Normalize off
   and on while playing changes the level at once.
7. **Silence trim (`--trim-silence`):** a file with a few seconds of leading and trailing silence:
   the **first** open already starts at the trimmed start and ends at the trimmed end (Log "Silence
   detection complete: first audible ..."), the shown duration is the trimmed one, seeking stays in
   the window, loop wraps within it. On BASS the trim applies only from the second open (ENG-040). A
   song with a count-in click in the leading silence: the click is trimmed.
8. **MIDI/KAR:** a `.kar` with lyrics and a `.mid` with lyric events (with `gm.dls`): it plays, the
   lyrics, line breaks, page changes and wipe timing look as on BASS, and the karaoke metadata (title,
   artist, key, tempo, time signature) matches. A Shift-JIS `.kar`. A `.rmi`. Tempo and pitch on a
   `.kar`.
9. **Archives:** a zip with MP3+CDG (graphics in sync with what you hear, compare with BASS), a zip
   with KAR+image background.
10. **Device:** while playing, change the default output device in Windows and unplug the headphones
    or USB DAC: playback moves to the new default without stopping; the Log shows "FatzAudio moved the
    output ..." / "... went away ..." and the host gets `AudioDeviceChanged`.
11. **Still on BASS with `--audio=fatz`:** a video plays with sound; a video in Audio mode
    (`--mode=audio`); an https radio stream; a plain http radio stream (now through FFmpeg: it plays,
    titles show).
12. **Formats:** a WMA file (FatzAudio decodes it through Media Foundation, never run on hardware by
    FatzAudio); an ALAC `.m4a` or DTS file (Log "FatzAudio cannot decode ...; playing an FFmpeg
    conversion" if FatzAudio refuses it). A tracker module (`.mod`, `.xm`): plays and seeks.
13. **Fallback:** rename `runtimes\win-x64\native\fatzaudio.dll` in the player's output folder and
    start with `--audio=fatz`: a `Bootstrap` warning "Audio engine FatzAudio could not start ...; using
    BASS", and audio plays on BASS. Put it back.
14. **Several players:** `--players=2 --audio=fatz`: both play independently.

## Next

- Owner smoke above, then decide: `MinSound` as public `SilenceTrimOptions` setting; whether
  normalization moves to FatzAudio's LUFS (slice 6, changes levels); mapping an explicit output
  device to FatzAudio's device list.
- Slice 3 (karaoke surfaces) and the slice-0 dropout rerun on 1.0.0-rc.1.

## Review notes (pre-PR, 2026-11-03)

Fixed before the PR: archive entries FatzAudio cannot decode go through the FFmpeg conversion as
on BASS; a normalization change no longer cuts a running `SlideVolume` short; closing waits for a
cancelled analysis before deleting an FFmpeg temp file; a late `Ended` after Play or a seek is
ignored; a trim window the stream refuses plays untrimmed instead of failing the open;
`CanOpenNetworkAudio` keeps the BASS path exactly as before when no audio pipeline is attached.

Known, not fixed in this slice:
- **One device change can reach the host twice with `--audio=fatz`:** the platform's BASS monitor
  (kept for video audio) and FatzAudio both report it, and the BASS notice's wording mentions BASS.
  Revisit with slice 5, when video audio moves off BASS.
- **Building the solution needs `FATZAUDIO_PACKAGES_TOKEN`:** the test players and Linux.Tests
  reference the FatzAudio package, which GitHub Packages serves only with a token, even to read.
- `AnalyzePeakAsync` called while the engine closes may throw instead of returning 0.
