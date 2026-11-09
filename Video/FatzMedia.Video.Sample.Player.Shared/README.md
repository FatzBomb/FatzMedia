# FatzMedia.Video.Sample.Player.Shared

The test players' shared view model (Stage 044A). Not packable; `net10.0`; references Core and
CommunityToolkit.Mvvm only (pinned in `scripts/check-core-boundary.ps1`).

- `ViewModels/PlayerViewModel`: one player's state and commands, written against
  `PlayerCore`: engine creation, transport, seek scrubbing, sync offsets, effects,
  equalizer, subtitles, visual transforms, prepared next and fades, network sources, player modes,
  flyout state and the diagnostics log. Every property changes on the UI thread; engine events are
  marshalled there. Status reads and scrub calls never block the UI thread.
- `Hosting/`: what a host supplies, bundled in `PlayerHost`: `IPlayerDispatcher` (UI thread and a
  timer), `IPlayerClipboard`, `IPlayerFilePicker`, `IPlayerEngineFactory` (renderer, platform
  package, native paths) and the debug log path. `PlayerDebugLog` and `PlayerNativePaths` are
  helpers for hosts.
- `PlayerStartupOptions`: the command line every player understands; hosts parse their own flags
  from the same arguments. `--lightweight-surfaces` (Stage 046A slice 2) asks each player for its
  smoke window of the adapter's `BackgroundSurface` and `KaraokeSurface`.

- `Presentation/` (Stage 046A slice 3, what the three hosts duplicated): `FullScreenChromeController`
  (the 2.5 s full-screen auto-hide of bars and cursor over the host's `IPlayerTimer`; only a real
  pointer move shows them, and the timer retries while the host keeps them: pointer on the bars,
  a flyout open, a drag), `ProcessSampler`, `PresentedFrameCounter` and `PlayerStatsLine` (the
  stats line, `PlayerViewModel.StatsText` / `UpdateStats`; fps from Core's
  `PresentationFramePresented`, the surface description from the host), and `PlayerScreenChooser`
  (where a second screen opens, over the screens' rectangles). `PlayerSetController<TSlot>`
  (ENG-039 R1) drives a window's 1-4 players: the coalescing rebuild when the toolbar changes,
  full screen with one F11/Escape rule, the 1 s stats tick and the closing sequence (wait for the
  rebuild, dispose every player, then close). The window implements `IPlayerSetHost<TSlot>`
  (create, attach, detach, layout, window full screen) and each player view `IPlayerSlot`.
- `PlayerRendererRequest` / `PlayerRendererRules`: the neutral `--renderer=` names and the GPU-first
  `auto` order; each host maps a request to its factories and logs what it chose.

## Command line (every player)

| Flag | Meaning |
| --- | --- |
| `[file\|url]`, `--url=` | Startup media for player 1 (a URL wins over a file). |
| `--players=1..4` | Independent players side by side (one view model each); default 1. |
| `--renderer=auto\|gpu\|warp\|software\|vulkan\|direct3d11`, `--software` | `auto` (default) is GPU first: on Windows Direct3D11 on the GPU, then WARP, then software; elsewhere Vulkan, then software. `gpu` = the platform's GPU renderer (Direct3D11 GPU on Windows, Vulkan elsewhere); `warp` and `direct3d11` (GPU else WARP, no software fallback) are Windows only; `--software` = `software` when `--renderer=` is absent or invalid. An unusable or unknown choice is logged and auto is used. |
| `--surface=auto\|framesink` | `framesink` forces the frame-sink path: WPF `WpfComposited`, Avalonia `PreferGpuSharedSurface = false`, WinForms `PreferNativeWindow = false`. |
| `--decoding=auto\|software` | `software` decodes in software whatever the renderer could take (CPU comparisons). |
| `--second-screen=<n>` | Player 1's second screen opens full screen on screen n at startup; the toggles use the same rule (screen n, else the first screen that is not the main window's, else a window). |
| `--hdr-output` | HDR output on HDR displays (Direct3D11 native window surfaces: WPF and WinForms; the Avalonia player warns and ignores it). |
| `--lightweight-surfaces` | A smoke window with the adapter's `BackgroundSurface` and `KaraokeSurface`. |
| `--no-auto-hide`, `--mode=`, `--native=`, `--net-*`, `--eq=`, `--prepare-next=`, `--auto-next`, `--fade-next=`, HDR and load-reset flags | As in `PlayerStartupOptions`. |

Every player: F11 or a double-click shows that player alone, full screen (bars and cursor hide
after 2.5 s without pointer movement); Escape closes the open flyout, then leaves full screen; a
file dropped on a player (including its video) opens there; the seek bar steps with the arrow keys
(5 s) and Page Up / Page Down (10 s) once focused; a stats line under each player shows state, fps,
process CPU and memory, and the surface's size and path (and the second screen's).

A new host implements the four interfaces, binds `PlayerViewModel`, calls `StartAsync()` once its
view is up, binds its surface to `Player`, and disposes the view model (`DisposeAsync` off the UI
thread) when it closes. Host-only features go in a derived view model (as the WPF player's
presentation mode) or in view code.
