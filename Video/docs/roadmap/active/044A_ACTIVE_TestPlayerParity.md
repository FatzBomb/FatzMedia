# Active Stage 044A - Test Player Parity (shared view model)

Prepared: 2026-10-26
Status: slice 1 implemented, pending manual smoke (`updates/044A_1`).
Owner decision (2026-10-26): the three test players (WPF, Avalonia, WinForms) match, and a new
host is cheap. The approach is one UI-agnostic view model that every host binds; the WPF player
is the reference and must not regress.

## Shape

- `FatzMedia.Video.Sample.Player.Shared` (net10.0, not packable; Core and
  CommunityToolkit.Mvvm only, pinned in `scripts/check-core-boundary.ps1`):
  - `ViewModels/PlayerViewModel` (+ partials per concern): engine creation, transport, seek
    scrubbing, sync offsets, effects, equalizer, subtitles, visual transforms, prepared next and
    fades, network sources, player modes, the diagnostics log, flyout state.
  - `Hosting/`: what a host supplies. `IPlayerDispatcher` (UI thread, timer),
    `IPlayerClipboard`, `IPlayerFilePicker`, `IPlayerEngineFactory` (renderer, platform package,
    native paths), bundled in `PlayerHost`; `PlayerDebugLog` (debug log file) and
    `PlayerNativePaths` (search list) helpers.
  - `PlayerStartupOptions`: the command line the players share.
- Hosts keep their framework concerns: surfaces, layout, full screen, second screen, file
  drops, and host-only options (WPF presentation mode and auto-hide; Avalonia players, surface
  path, renderers; WinForms Direct3D11 device choice).

## Acceptance (slice 1)

1. The shared project builds for net10.0 with no UI-framework, adapter, platform or renderer
   reference; the boundary scripts pass.
2. The WPF player binds the shared view model with its XAML and look unchanged; the commit that
   moves it changes no behaviour.
3. Every host scrubs while dragging the seek bar (`BeginScrub`, drag seeks, `EndScrub`); no
   scrub or status call blocks a UI thread.
4. The Avalonia player reaches WPF feature parity (Sync, Video, Audio, A/V, Next and Log
   features) through MVVM bindings, keeping `--players`, `--second-screen`, `--surface`,
   `--renderer`, per-player mode, the URL box, copy log and the process sampler.
5. The WinForms player reaches the same parity with manual bindings.
6. The shared flags mean the same in every host; existing flags keep working.
7. The view model's logic is unit-tested over the engine test doubles; Release build is
   warning-clean; Portable, Linux, Avalonia and Vulkan tests pass; `dotnet format` is clean.
8. Manual smoke on Windows (WPF, Avalonia, WinForms) passes (`updates/044A_1`).

## Not in this stage

- A shared XAML layer (WPF and Avalonia XAML differ); each host lays out its own controls.
- WPF-only features in other hosts: the presentation mode (native HWND overlay or composited)
  and surface auto-hide belong to the WPF `MediaSurface`.
- Player-level changes to the engine: none were needed.
