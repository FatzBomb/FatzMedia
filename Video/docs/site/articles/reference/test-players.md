# Test players

The repository contains three small player applications, one for each UI framework the library supports: WPF, WinForms and Avalonia. They are the library's own test rigs, and they are also the best runnable samples: each is a complete host that creates players, binds surfaces, switches renderers, shows diagnostics and opens a second screen. Read their source to see how a real application wires the library together.

| Player | Project | UI framework | Platforms |
| --- | --- | --- | --- |
| WPF | `FatzMedia.Video.Sample.Player` | WPF | Windows x64 (`net10.0-windows7.0`) |
| WinForms | `FatzMedia.Video.Sample.Player.WinForms` | WinForms | Windows x64 (`net10.0-windows7.0`) |
| Avalonia | `FatzMedia.Video.Sample.Player.Avalonia` | Avalonia | Linux (`net10.0`) and Windows (`net10.0-windows7.0`) |

All three bind one shared view model, `PlayerViewModel` in the `FatzMedia.Video.Sample.Player.Shared` project. It holds the player logic (creating the engine, transport, seeking, sync offsets, effects, equalizer, subtitles, visual transforms, prepared next item and fades, network sources, player modes, the diagnostics log). Each host supplies only its UI thread, dialogs, engine factory and surface. Use it as a model for an MVVM application.

## Build and run

You need the .NET 10 SDK and the FFmpeg native libraries (the repository does not contain FFmpeg binaries). The audio engine, FatzAudio, is built with the repository.

1. Obtain the FFmpeg 9.0.x shared DLLs (`avcodec-63.dll` and the six others). On Linux use the matching shared libraries (`libavcodec.so.63` and the rest).
2. Put them in the player project's `native/` folder (for example `FatzMedia.Video.Sample.Player/native/`). They are copied next to the application on build and publish. Alternatively pass `--native=<dir>` (repeatable, highest priority first).
3. Build and run.

```powershell
# WPF
dotnet run --project FatzMedia.Video.Sample.Player -c Release -- "C:\media\song.mp4"

# WinForms
dotnet run --project FatzMedia.Video.Sample.Player.WinForms -c Release -- "C:\media\song.mp4"

# Avalonia on Windows
dotnet run --project FatzMedia.Video.Sample.Player.Avalonia -c Release -f net10.0-windows7.0 -- "C:\media\song.mp4"
```

```bash
# Avalonia on Linux
dotnet run --project FatzMedia.Video.Sample.Player.Avalonia -c Release -f net10.0 -- ./song.mp4

# Headless Linux box
xvfb-run -a dotnet run --project FatzMedia.Video.Sample.Player.Avalonia -c Release -f net10.0
```

On Linux the Avalonia player also looks in the directories named by the environment variables `FATZBOMB_LINUX_FFMPEG_DIR`. The repository script `scripts/setup-linux-native.sh` downloads FFmpeg 9.0 LGPL libraries and test media into `/opt/fatzbomb-native`. The Vulkan renderer on a machine without a GPU needs Mesa's lavapipe driver (`mesa-vulkan-drivers` on Debian and Ubuntu). FatzAudio's library comes from the repository build; without it the player starts and plays video without sound.

Without the native libraries the players still start, the panels stay disabled, and the Log lists exactly which library is missing. See [Common problems](../troubleshooting/common-problems.md).

## Command line

Every player understands the same flags (parsed by `PlayerStartupOptions` in `FatzMedia.Video.Sample.Player.Shared`). Flags with a value use `--name=value`. Names are case-insensitive. Unknown flags are ignored. An unknown value of `--renderer`, `--surface` or `--decoding` is logged as a warning and the default is used.

| Flag | Meaning |
| --- | --- |
| `<file>` or `<url>` | The first argument that is not a flag. A path is loaded at startup by player 1. An `http` or `https` URL is opened as a network source. |
| `--url=<url>` | An `http` or `https` source for player 1. It wins over a positional file. |
| `--mode=full\|background\|audio` | The initial [player mode](../playback/player-modes.md) (default `full`). Each player's Mode box switches it at run time. |
| `--native=<dir>` | A native library folder, repeatable. Searched before the defaults. |
| `--players=1..4` | How many independent players are shown side by side (default 1). The toolbar changes the count at run time and recreates the players. |
| `--renderer=auto\|gpu\|warp\|software\|vulkan\|direct3d11` | The renderer. `auto` (default) is GPU first: Direct3D11 on the GPU, then WARP, then software on Windows; Vulkan, then software elsewhere. `gpu` is the platform's GPU renderer. `warp` and `direct3d11` exist on Windows only; `warp` decodes in software. See [Renderers](../video/renderers.md). |
| `--software` | The software renderer, when `--renderer=` is absent or invalid. |
| `--surface=auto\|framesink` | `framesink` forces the frame-sink path: `WpfComposited` in WPF, `PreferGpuSharedSurface = false` in Avalonia, `PreferNativeWindow = false` in WinForms. For comparisons. See [Surfaces](../presentation/surfaces.md). |
| `--decoding=auto\|software` | `software` decodes in software whatever the renderer could take, to compare CPU load. |
| `--second-screen=<n>` | Opens player 1's second screen full screen on screen n (1-based) at startup. See [Second screen](../presentation/second-screen.md). |
| `--hdr-output` | HDR output on HDR displays. Direct3D11 native-window surfaces only (WPF and WinForms); the Avalonia player logs that it is not available and ignores it. See [Display and color](../video/display-and-color.md). |
| `--no-hdr-tonemap` | Show HDR video without tone mapping, for comparison. |
| `--hdr-tonemap-cpu` | Tone map HDR video on the CPU, for comparison. |
| `--lightweight-surfaces` | Opens a smoke window with the adapter's `BackgroundSurface` and `KaraokeSurface`. |
| `--audio-device=<id or name>` | The FatzAudio output device (see [Output devices](../audio/output-devices.md)). The Log lists the names when it is not found. The default is the system default device. |
| `--cdg-scale[=0..10]` | The scaled CD+G picture; the value is the smoothness. Off (the classic picture) without it. |
| `--cdg-effects` | Adds an outline and a shadow to the scaled CD+G picture. |
| `--video-audio=fatz` | FatzAudio decodes video soundtracks from FFmpeg's packets; FFmpeg decodes otherwise. |
| `--no-auto-hide` | Surfaces keep the last frame after stop (`AutoHide = false`). |
| `--net-retries=<n>` | Retries when opening a network source (`NetworkRetryPolicy.MaxRetries`). |
| `--net-ca-file=<path>` | An extra certificate authority file for `https` (`NetworkTlsPolicy.CertificateAuthorityFile`). |
| `--net-allow-invalid-certs` | Accept invalid `https` certificates (`NetworkTlsPolicy.AllowInvalidCertificates`). |
| `--trim-silence` | Trim leading and trailing silence (`SilenceTrimOptions.Enabled`). |
| `--background-follows-media` | A background video follows the song: waits for Play, pauses with it, rewinds on Stop and at the end, restarts when it loops (`BackgroundFollowsMedia`). By default it loops on its own; the **BG** / **BG pause** button pauses it explicitly. |
| `--reset-pitch-on-load`, `--reset-tempo-on-load` | `ResetPitchOnMediaLoad` and `ResetTempoOnMediaLoad`. |
| `--eq=g1,...,g10` | Ten equalizer band gains in dB. Giving it turns the equalizer on. |
| `--prepare-next=<file>` | Prepare an item to play next. |
| `--auto-next` | Start the prepared item when the current one ends. |
| `--fade-next=<seconds>` | Fade length into the prepared item, 0 to 60. |
| `--visual-transform-demo` | Start with demonstration color-transform settings. |

## What they demonstrate

- **Player and surface wiring.** Creating a player off the UI thread (`Player.CreateAsync`), binding a `MediaSurface`, disposing off the UI thread, marshaling events to the UI thread.
- **Renderer choice.** A fallback chain (Direct3D11 on the GPU, then WARP, then software), with the toolbar switching the renderer by recreating the players. The Log panel's `Renderer` entries show what was probed and why a renderer was chosen.
- **Presentation paths.** The stats line under each player ends with the surface's size and path; for example `via shared GPU image` or `via frame sink`.
- **Second screen.** A button opens a `SecondaryPresentationWindow` for each player. F11 or a double-click toggles full screen; Escape leaves it or closes the window.
- **Several players.** `--players=4` is a measuring rig: per-player fps, process CPU and memory, per-player logs.
- **Diagnostics.** A Log flyout lists every `MediaDiagnostic` and `MediaError` event, newest first, with level colors, Copy all and Open log file. Every diagnostic is also written to a file in `%TEMP%\FatzMedia.Video` (`player-debug.log` for WPF, `player-winforms-debug.log`, `player-avalonia-debug.log`), overwritten at each start.
- **Playback features.** Transport, seek bar with scrubbing, volume, pitch, tempo, normalization, equalizer, sync offsets, subtitle picker, color transforms, display modes, display-locked timing, prepared next item with fades, network sources, player modes.
- **Full screen.** F11 or a double-click shows one player full screen; the controls hide after 2.5 seconds without mouse movement; Escape closes an open flyout, then leaves full screen.
- **Drag and drop.** Dropping a file on a player opens it there.
