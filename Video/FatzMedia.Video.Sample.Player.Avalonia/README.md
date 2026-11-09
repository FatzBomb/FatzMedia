# FatzMedia.Video.Sample.Player.Avalonia

Avalonia test player for `FatzMedia.Video`, for Windows and Linux. It exercises the Avalonia
`MediaSurface` adapter with the frame-sink renderers (software and Vulkan) and doubles as a small
measuring rig for several players in one process.

Each player binds the shared `PlayerViewModel` (`FatzMedia.Video.Sample.Player.Shared`, Stage
044A), the same view model as the WPF and WinForms players, so it has the WPF player's features:
the **Sync** (offsets, subtitle picker), **Video** (visual transforms), **Audio** (pitch, tempo,
normalize, load resets, silent drag, 10-band EQ), **A/V** (auto play, display-locked timing,
display mode, CD+G background), **Next** (prepared item, auto-next, fades, stream info) and
**Log** flyouts. The seek bar scrubs while dragging (`BeginScrub`, drag seeks, `EndScrub`); a
key steps it once. Drop a file on a player to open it there. A double-click on the picture (or
F11) shows that player alone, full screen, with the controls hidden after 2.5 s without mouse
movement; Escape closes the open flyout, then leaves full screen. Every diagnostic also goes to
`%TEMP%/FatzMedia.Video/player-avalonia-debug.log` (**Open log file**), overwritten at each start.

## What it tests

- `FatzMedia.Video.Avalonia.MediaSurface` bound to a `MediaControlPlayer` (composited frame sink).
- Software and Vulkan renderers, and Direct3D11 on the GPU and on WARP in the Windows build,
  switched at run time (an unusable renderer is greyed out). `--hdr-output` is logged as not
  available (the surfaces are composited) and ignored.
- The surface's presentation path (Stage 027A slice 2): the status line ends with `via shared GPU image`
  (frames imported by Avalonia's compositor, no CPU copy) or `via frame sink`, and the log gives the
  reason. `--surface=framesink` forces the frame sink, for comparison.
- Video decoding (Stage 045A): with Direct3D11 on a GPU the player decodes with D3D11VA where the GPU
  supports the codec (the log's `Decoding ...` line ends `via d3d11va`). The frames stay on the GPU
  into the shared GPU image, or are read back once for the frame sink. With Vulkan on a GPU with Vulkan
  Video (Stage 045A slices 2-3) it decodes on the renderer's device (`via vulkan`); on Windows the
  Vulkan frames also reach the compositor as a shared GPU image (keyed-mutex Direct3D11 textures). On WARP, with the
  software renderer, and with Vulkan without Vulkan Video, it decodes in software; the renderer line in
  the log says which and why.
  `--decoding=software` forces software decoding, for CPU comparisons.
- Open (file picker, filter from the engine), a URL box with **URL...** for http/https sources (MP4/MKV
  progressive or HLS `.m3u8`, Stage 039A; capabilities and buffering are logged), Play/Pause, Stop, seek bar,
  volume, pitch (-12..+12 semitones), tempo (50..200 %), loop.
- **Second screen** (Stage 027A slice 5): a box per player opens the adapter's
  `SecondaryPresentationWindow` for that player, full screen on the screen given by
  `--second-screen=<n>`, else on the first screen that is not the main window's, else (one
  screen) in a 960x540 window. The status line adds `2nd screen WxH via <path>`; the log gives
  its path and reason. F11 or a double-click toggles full screen; Escape leaves it,
  or closes the window when it is not full screen.
- A **Log** flyout per player (open at start): every `MediaDiagnostic` and `MediaError` event, newest
  first (1000 entries). **Copy all** puts the whole log, oldest first, on the clipboard (for a bug
  report).
- **Players: 1-4** in the toolbar: that many independent players in a grid, each with its own engine,
  surface, file, transport and log. Changing the count or renderer recreates the players (files are not kept).
- A status line per player: state, frames presented per second (from `PresentationFramePresented`), process CPU
  (share of all cores, plus cores used), managed memory (`GC.GetTotalMemory`) and working set, and the
  surface pixel size. CPU and memory are for the whole process, so they are the same on every line;
  fps is per player. Sampled once a second. Above it, the view model's status line shows the latest
  status, warning or error. The seek bar's 200 ms timer reads `State`, `Duration` and `Position`
  off the UI thread (ENG-035, now in the shared view model), so an engine read that blocks or throws
  cannot freeze the window: a read slower than 100 ms, blocked for a second, or failing is logged
  as a Warning.

## Run

The app multi-targets because the platform packages do (see below). Pick the TFM for your OS:

```bash
# Linux
dotnet run --project FatzMedia.Video.Sample.Player.Avalonia -c Release -f net10.0 -- [media-file] [options]
# Windows
dotnet run --project FatzMedia.Video.Sample.Player.Avalonia -c Release -f net10.0-windows7.0 -- [media-file] [options]
```

Options (all shared with the other players since Stage 046A slice 3, see
`FatzMedia.Video.Sample.Player.Shared/README.md`): `--renderer=auto|gpu|warp|software|vulkan|direct3d11`
(default auto, GPU first: Direct3D11 on the GPU, then WARP, then software in the Windows build;
Vulkan, then software elsewhere; `--software` is `software`; `warp` and `direct3d11` in the Windows
build only; an unusable choice falls back to auto with a warning), `--surface=auto|framesink`
(default auto), `--decoding=auto|software` (default auto: hardware for Direct3D11 on a GPU, as
above), `--players=1..4`, `--second-screen=<n>` (1-based screen number; opens player 1's
second screen there at startup, full screen), and the flags every test player shares
(`PlayerStartupOptions`): an optional media file or http/https URL for the first player
(`--url=<http(s)://...>` does the same and wins over a file), `--mode=full|background|audio`
(Stage 043A; default full), `--native=<dir>` (repeatable), `--net-retries=`, `--net-ca-file=`,
`--net-allow-invalid-certs`, `--no-hdr-tonemap`, `--hdr-tonemap-cpu`, `--trim-silence`,
`--reset-pitch-on-load`, `--reset-tempo-on-load`, `--eq=g1,...,g10`, `--prepare-next=`,
`--auto-next`, `--fade-next=`, `--visual-transform-demo`, `--no-auto-hide` (surfaces keep the
last frame after stop; Stage 046A), `--lightweight-surfaces` (a smoke window with the adapter's
`BackgroundSurface` and `KaraokeSurface`, as in every player; Stage 046A slice 2). Each player's Mode box switches its mode on the fly
(`SetModeAsync`); `--mode` sets its initial value.

On a headless Linux box: `xvfb-run -a dotnet .../FatzMedia.Video.Sample.Player.Avalonia.dll`.
Vulkan on a machine without a GPU needs Mesa lavapipe (`apt-get install mesa-vulkan-drivers`).

## Why two target frameworks

`FatzMedia.Video.Windows` targets `net10.0-windows7.0`; `FatzMedia.Video.Linux` targets `net10.0`.
A `net10.0` project cannot reference a Windows-TFM project, so the player targets both and each TFM
references exactly one platform package. `PlatformComposition.cs` picks the factory with `#if WINDOWS`
(`WindowsPlatformFactory` or `LinuxPlatformFactory`, each given the chosen renderer
factory). Everything else is shared source. The `net10.0` build refuses to run on a non-Linux OS with a
message pointing to the Windows build. The Windows build also references `FatzMedia.Video.Windows.Direct3D11`
for the Direct3D11 renderer, which feeds the surface's shared GPU image path (DXGI keyed-mutex textures).

## Native libraries

The packages ship no BASS or FFmpeg binaries. Directories are searched in this order; only existing
ones are passed to the engine:

1. `--native=<dir>` arguments, in order;
2. `FATZBOMB_LINUX_FFMPEG_DIR` (the variable the Linux tests use) and `FATZBOMB_LINUX_BASS_DIR`;
3. `native/` next to the executable (copied from `FatzMedia.Video.Sample.Player.Avalonia/native/` at
   build/publish; created empty if missing) and `runtimes/<rid>/native`.

Needed: BASS and BASS_FX (`libbass.so`/`libbass_fx.so` or the x64 DLLs), optional BASS MIDI/add-ons, and
FFmpeg 9.x (`libavcodec.so.63` ABI / `avcodec-63.dll`). See `docs/NATIVE_DEPENDENCIES.md`. Without them the
panels stay disabled and the log lists exactly which library is missing.

## Notes

- Engine events arrive on background threads; the shared view model marshals them with the host's
  dispatcher (`AvaloniaPlayerHost.cs`: `Dispatcher.UIThread`).
- Players are created sequentially off the UI thread and disposed off the UI thread (dispose waits for media operations).
- MVVM: `PlayerPanel.axaml` binds the shared view model (compiled bindings); the code-behind keeps
  only view behaviour (seek drag, second screen, full screen, drops, surface path, frame rate).
  The app uses only the public engine API, the Avalonia adapter, a platform package, renderer
  packages and the shared view model.
