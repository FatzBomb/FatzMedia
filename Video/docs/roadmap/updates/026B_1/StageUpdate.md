# Stage Update

Stage: 026B.1 - Linux platform-services baseline
Date: 2026-10-16

## Status: IMPLEMENTED - native FFmpeg/decoder validated on Linux; BASS/player smoke pending

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
