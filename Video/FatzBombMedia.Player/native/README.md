# Host Native DLL Drop Folder

This is the host app's design-time native DLL folder.

Put the x64 BASS and FFmpeg DLLs here:

```text
FatzBombMedia.Player/native/
  bass.dll
  bass_fx.dll
  avcodec-62.dll
  avdevice-62.dll
  avfilter-11.dll
  avformat-62.dll
  avutil-60.dll
  swresample-6.dll
  swscale-9.dll
```

`FatzBombMedia.Player.csproj` copies `native/**/*.dll` to the player output folder, preserving the `native/` subfolder.

At runtime these files are copied beside `FatzBombMedia.Player.exe`:

```text
native/
```

`PlayerNativeLibraries` resolves that runtime folder and passes it into the WPF `Player`, which inherits the Core `MediaControlPlayer`.
