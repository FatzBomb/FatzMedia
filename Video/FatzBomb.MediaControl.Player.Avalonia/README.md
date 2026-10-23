# FatzBomb.MediaControl.Player.Avalonia

Avalonia test player for `FatzBomb.MediaControl`, for Windows and Linux. It exercises the Avalonia
`MediaSurface` adapter with the frame-sink renderers (software and Vulkan) and doubles as a small
measuring rig for several players in one process.

## What it tests

- `FatzBomb.MediaControl.Avalonia.MediaSurface` bound to a `MediaControlPlayer` (composited frame sink).
- Software and Vulkan renderers, switched at run time (Vulkan is greyed out when no loader/device is usable).
- Open (file picker, filter from the engine), Play/Pause, Stop, seek bar, volume, pitch (-12..+12
  semitones), tempo (50..200 %), loop.
- A log panel per player: every `MediaDiagnostic` and `MediaError` event, newest first (500 entries).
- **Players: 1-4** in the toolbar: that many independent players in a grid, each with its own engine,
  surface, file, transport and log. Changing the count or renderer recreates the players (files are not kept).
- A status line per player: state, frames presented per second (from `PresentationFramePresented`), process CPU
  (share of all cores, plus cores used), managed memory (`GC.GetTotalMemory`) and working set, and the
  surface pixel size. CPU and memory are for the whole process, so they are the same on every line;
  fps is per player. Sampled once a second.

## Run

The app multi-targets because the platform packages do (see below). Pick the TFM for your OS:

```bash
# Linux
dotnet run --project FatzBomb.MediaControl.Player.Avalonia -c Release -f net10.0 -- [media-file] [options]
# Windows
dotnet run --project FatzBomb.MediaControl.Player.Avalonia -c Release -f net10.0-windows7.0 -- [media-file] [options]
```

Options: `--renderer=software|vulkan|auto` (auto = Vulkan if usable, else software; default software),
`--players=1..4`, `--native=<dir>` (repeatable), and an optional media file for the first player.

On a headless Linux box: `xvfb-run -a dotnet .../FatzBomb.MediaControl.Player.Avalonia.dll`.
Vulkan on a machine without a GPU needs Mesa lavapipe (`apt-get install mesa-vulkan-drivers`).

## Why two target frameworks

`FatzBomb.MediaControl.Windows` targets `net10.0-windows7.0`; `FatzBomb.MediaControl.Linux` targets `net10.0`.
A `net10.0` project cannot reference a Windows-TFM project, so the player targets both and each TFM
references exactly one platform package. `PlatformComposition.cs` picks the factory with `#if WINDOWS`
(`WindowsMediaControlPlatformFactory` or `LinuxMediaControlPlatformFactory`, each given the chosen renderer
factory). Everything else is shared source. The `net10.0` build refuses to run on a non-Linux OS with a
message pointing to the Windows build. Direct3D11 is not offered: it presents to native windows, which
the composited Avalonia surface does not use (use the WPF or WinForms player for it).

## Native libraries

The packages ship no BASS or FFmpeg binaries. Directories are searched in this order; only existing
ones are passed to the engine:

1. `--native=<dir>` arguments, in order;
2. `FATZBOMB_LINUX_FFMPEG_DIR` (the variable the Linux tests use) and `FATZBOMB_LINUX_BASS_DIR`;
3. `native/` next to the executable (copied from `FatzBomb.MediaControl.Player.Avalonia/native/` at
   build/publish; created empty if missing) and `runtimes/<rid>/native`.

Needed: BASS and BASS_FX (`libbass.so`/`libbass_fx.so` or the x64 DLLs), optional BASS MIDI/add-ons, and
FFmpeg 9.x (`libavcodec.so.63` ABI / `avcodec-63.dll`). See `docs/NATIVE_DEPENDENCIES.md`. Without them the
panels stay disabled and the log lists exactly which library is missing.

## Notes

- Engine events arrive on background threads and are marshalled with `Dispatcher.UIThread.Post`.
- Players are created sequentially off the UI thread and disposed off the UI thread (dispose waits for media operations).
- All code is plain code-behind; the app uses only the public engine API, the Avalonia adapter, a platform package and renderer packages.
