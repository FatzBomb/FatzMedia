# Host Native DLL Drop Folder

This is the Player's design-time native folder. The packages do not supply these
binaries: obtain and license them according to `docs/NATIVE_DEPENDENCIES.md`. Use x64
builds.

## Required

The Player needs these files to start playback:

```text
FatzBomb.MediaControl.Player/native/
  bass.dll          audio playback
  bass_fx.dll       tempo, pitch, equalizer, and fades
  avcodec-63.dll    FFmpeg 9.0.x (FFmpeg 8 DLLs no longer load)
  avdevice-63.dll
  avfilter-12.dll
  avformat-63.dll
  avutil-61.dll
  swresample-7.dll
  swscale-10.dll
```

All seven FFmpeg DLLs must come from the same build.

## Optional

Each BASS plugin adds file types. When one is missing, FFmpeg decodes those formats
instead (Stage 033A), so they still open; BASS's own decoder is just faster to start and
streams instead of converting. The exceptions are MIDI and CD tracks, which FFmpeg
cannot play: without `bassmidi.dll` or `basscd.dll` those files do not open.

| File | Adds |
|---|---|
| `bass_aac.dll` | `.aac`, `.m4a`, `.m4b` |
| `bassalac.dll` | Apple Lossless `.m4a` |
| `bassflac.dll` | `.flac` |
| `bassmidi.dll` | `.mid`, `.midi`, `.kar`, `.rmi` (MIDI karaoke) |
| `bassopus.dll` | `.opus` |
| `bass_ac3.dll` | `.ac3` |
| `basswma.dll` | `.wma` |
| `basscd.dll` | audio CD tracks (`.cda`) |
| `TimGM6mb.sf2` | Fallback MIDI soundfont. It is used only when Windows' `gm.dls` is missing and no soundfont is configured. Check its licence before redistributing it. |

## Not used

The app never runs the FFmpeg command-line tools (`ffmpeg.exe`, `ffplay.exe`,
`ffprobe.exe`). It probes and decodes media in-process through the DLLs above. Install
the tools on your PC for manual inspection (for example `winget install Gyan.FFmpeg`)
instead of keeping them here.

## How the folder reaches the app

`FatzBomb.MediaControl.Player.csproj` copies `native/**/*.dll` and `native/**/*.sf2` to
a `native/` folder beside `FatzBomb.MediaControl.Player.exe`. `PlayerNativeLibraries`
resolves that folder and passes it to the WPF `Player`, which inherits the Core
`MediaControlPlayer`.

## Licensing

Licensing matters only if you distribute the app. Gyan "full" FFmpeg builds are
compiled with `--enable-gpl`. An LGPL build (see `docs/NATIVE_DEPENDENCIES.md`) is the
default choice for a distributed host. BASS requires a licence from un4seen for
commercial use.
