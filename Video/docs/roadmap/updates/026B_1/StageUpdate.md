# Stage Update

Stage: 026B.1 - Linux platform-services baseline
Date: 2026-10-16

## Status: IMPLEMENTED - Linux smoke with BASS passed 2026-11-05 (steps 1-3, 5); step 4 see below; step 6 pending on Windows

Delivered:

- New package `FatzBomb.MediaControl.Linux` (`LinuxMediaControlPlatformFactory`,
  `LinuxPlatformOptions`, internal bootstrapper, precision waiter, refresh timing,
  BASS device monitor, MIDI lyric provider, software decode capability scan).
- New test project `FatzBomb.MediaControl.Linux.Tests` (40 cases) with opt-in native
  smokes (`FATZBOMB_LINUX_FFMPEG_DIR`, `FATZBOMB_LINUX_SAMPLE_VIDEO`).
- Core: sidecar matching fix for case-sensitive file systems (+4 portable tests); the
  previously failing portable sidecar test now passes on Linux.
- CI: new `linux-portable` job on `ubuntu-latest` runs the portable and Linux test
  projects.
- Boundary script DAG/IVT allowlist updated for the new projects (test friends only).

## Linux sandbox validation (2026-10-16)

- Release build of all portable projects: 0 warnings/errors.
- Portable tests 600/600 on Linux; Linux tests 40/40.
- Native smoke with FFmpeg 8.1 LGPL shared libraries (local only, not committed):
  dependency-order dlopen + FFmpeg.AutoGen activation + full codec/container scan
  (h264 software decoder, `.mp4` container found); Core `VideoDecoder` decoded a
  generated 320x240 MPEG-4 clip to non-blank BGRA frames. Negative check: a bogus
  directory fails the smoke.
- BASS for Linux could not be downloaded in the sandbox (proxy blocks un4seen.com), so
  BASS loading, audio output, and a full `MediaControlPlayer` run are not validated.

## Decisions made without the owner

- No implicit system library search (host-owned binaries policy).
- 60 Hz default refresh as a host option.
- macOS deferred; Linux first per the session instructions.

## Manual smoke (Linux desktop with BASS for Linux + FFmpeg 9.0.x shared libraries)

The engine needs FFmpeg 9 since the FFmpeg.AutoGen 9 upgrade; the validation results above
were recorded with FFmpeg 8.1 and stay as measured.

1. Put `libbass.so`, `libbass_fx.so`, optional add-ons, and FFmpeg 9 libs in an app
   `native/` folder. Compose `new LinuxMediaControlPlatformFactory(new
   SoftwarePresentationCoordinatorFactory())` and create a `MediaControlPlayer`.
   `LastNativeLoadMessage` reports both directories.
2. Play an MP3 and a FLAC: audio out through PulseAudio/PipeWire; Pause/Seek/Stop.
3. Play an MP4: software-renderer frames arrive (frame sink) and A/V stays in sync.
4. Unplug/switch the output device: `AudioDeviceChanged` fires.
5. Run the opt-in smokes:
   `FATZBOMB_LINUX_FFMPEG_DIR=<dir> FATZBOMB_LINUX_SAMPLE_VIDEO=<clip.mp4> dotnet test FatzBomb.MediaControl.Linux.Tests`.
6. Windows regression: sidecar backgrounds (including `song.PNG`) still attach.

## Review follow-up 2 (same day)

- Sidecar lookup lists the directory only on case-sensitive file systems (Linux); Windows
  and macOS keep the original exact-name probes, so large library folders load as fast as
  before there.

## Linux smoke run (2026-11-05, sandbox, BASS 2.4 for Linux + FFmpeg 9.0)

Run by Claude in the Linux sandbox once `www.un4seen.com` was allowed: BASS, BASS_FX and BASSFLAC for
Linux x86_64, FFmpeg 9.0 LGPL, PulseAudio with two null sinks ("Speakers", "Headphones"), ALSA's default
routed to PulseAudio (`libasound2-plugins`, `pcm.!default { type pulse }`). Repeatable as
`LinuxBassNativeTests` (opt-in: `FATZBOMB_LINUX_BASS_DIR`, `FATZBOMB_LINUX_FFMPEG_DIR`;
`scripts/setup-linux-native.sh` downloads BASS and makes the media).

1. **Native load: pass.** "Native libraries loaded. BASS: /opt/fatzbomb-native/bass; FFmpeg:
   /opt/fatzbomb-native/ffmpeg/lib."; audio available, required libraries loaded.
2. **MP3 and FLAC through PulseAudio: pass.** Both play at 1.000x real time, pause holds the position,
   seek to 6 s lands, resume and stop work. PulseAudio showed the player's stream on the "Speakers" sink
   (float32, stereo, 44.1 kHz) while it played.
3. **MP4 frames on the frame sink, on the audio clock: pass.** 30.0 frames per second presented, clock
   rate 0.999x, frames not blank.
4. **Output device change: playback is right, the event is not raised.** Switching PulseAudio's default
   to "Headphones" and then removing "Speakers" while a song played: PulseAudio moved the stream itself,
   playback continued without a gap to the natural end, and no `AudioDeviceChanged` was raised. Cause:
   on Linux the sound server (PulseAudio, PipeWire) reroutes streams; BASS keeps seeing the same ALSA
   default device, so the BASS device monitor sees no change (the same as FatzAudio, `updates/037A_2`).
   The step's pass criterion assumed Windows-style device events. Users get the right behaviour; only
   hosts that want to show "output moved" get nothing on Linux. Recorded in ENG-040 as a known gap.
5. **Linux tests: pass** (145 earlier this day, plus the 4 new BASS tests).
6. Windows sidecar backgrounds: in `scripts/smoke-backlog.ps1` test 12.

Not covered: a real desktop with speakers (null sinks consume audio in real time but nothing is heard),
and PipeWire (PulseAudio only). The result for steps 1-5 lets 026B move to complete once step 6 passes
on Windows.
