# Host Native DLL Drop Folder

This is the Player's design-time native folder. The packages do not supply these
binaries: obtain and license them according to `docs/NATIVE_DEPENDENCIES.md`. Use x64
builds.

## Required

The Player needs these files to start playback:

```text
FatzMedia.Video.Sample.Player/native/
  avcodec-63.dll    FFmpeg 9.0.x (FFmpeg 8 DLLs no longer load)
  avdevice-63.dll
  avfilter-12.dll
  avformat-63.dll
  avutil-61.dll
  swresample-7.dll
  swscale-10.dll
```

All seven FFmpeg DLLs must come from the same build.

## Audio

Audio needs nothing in this folder. The audio engine, FatzAudio, is built from `Audio\` in
the repository and its library (`fatzaudio.dll`) is copied next to the app by the build.
FFmpeg decodes the audio formats FatzAudio cannot open. BASS was removed in 037A: any
`bass*.dll` still in this folder is not used and can be deleted.

MIDI plays with the system General MIDI bank (Windows `gm.dls`). To use a soundfont file
instead, set `MediaPlayerConfiguration.MidiSoundfontPath` to it. `TimGM6mb.sf2` in this
folder is not picked up automatically; check its licence before redistributing it.

## Not used

The app never runs the FFmpeg command-line tools (`ffmpeg.exe`, `ffplay.exe`,
`ffprobe.exe`). It probes and decodes media in-process through the DLLs above. Install
the tools on your PC for manual inspection (for example `winget install Gyan.FFmpeg`)
instead of keeping them here.

## How the folder reaches the app

`FatzMedia.Video.Sample.Player.csproj` copies `native/**/*.dll` and `native/**/*.sf2` to
a `native/` folder beside `FatzMedia.Video.Sample.Player.exe`. `PlayerNativeLibraries`
resolves that folder and passes it to the WPF `Player`, which inherits the Core
`PlayerCore`.

## Licensing

Licensing matters only if you distribute the app. Gyan "full" FFmpeg builds are
compiled with `--enable-gpl`. An LGPL build (see `docs/NATIVE_DEPENDENCIES.md`) is the
default choice for a distributed host.
