# Host Native DLL Drop Folder

> **WARNING — GPL-ENABLED VALIDATION FIXTURE ONLY**
>
> `ffmpeg.exe`, `ffplay.exe`, and `ffprobe.exe` are **tracked in this repository** as a
> Gyan full-shared FFmpeg build compiled with `--enable-gpl --enable-version3`. They are
> **repository-only validation fixtures** for the local Player. They must **not** be
> copied, bundled, distributed, or republished with any package or host application
> unless that host deliberately accepts and complies with the resulting GPL obligations.
> Their presence here is under review; do not delete or replace them without owner
> approval of the licensing/distribution policy (see `docs/NATIVE_DEPENDENCIES.md`).

This is the host app's design-time native DLL folder.

Put the x64 BASS and FFmpeg 9.0.x shared DLLs here (FFmpeg 8 DLLs no longer load):

```text
FatzBomb.MediaControl.Player/native/
  bass.dll
  bass_fx.dll
  avcodec-63.dll
  avdevice-63.dll
  avfilter-12.dll
  avformat-63.dll
  avutil-61.dll
  swresample-7.dll
  swscale-10.dll
```

`FatzBomb.MediaControl.Player.csproj` copies `native/**/*.dll` to the player output folder, preserving the `native/` subfolder.

At runtime these files are copied beside `FatzBomb.MediaControl.Player.exe`:

```text
native/
```

`PlayerNativeLibraries` resolves that runtime folder and passes it into the WPF `Player`, which inherits the Core `MediaControlPlayer`.

The packages do not supply these binaries. Obtain and license them according to
`docs/NATIVE_DEPENDENCIES.md`. The repository's current Gyan full FFmpeg fixture is
GPL-enabled and is for validation only unless the distributing host accepts and
complies with those terms. The tracked `ffmpeg.exe`/`ffplay.exe`/`ffprobe.exe`
executables carry the same restriction and must not leave this repository.
