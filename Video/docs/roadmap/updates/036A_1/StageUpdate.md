# Stage Update

Stage: 036A.1 - Renderer fallback chain (GPU, WARP, software)
Date: 2026-10-18

## Status: IMPLEMENTED - owner smoke passed 2026-10-18

Owner smoke (RTX 5090, ultrawide): default picked `Direct3D11: GPU, NVIDIA GeForce RTX 5090,
feature level Level_11_0`; `--renderer=warp` played AV1 1080p (libdav1d) windowed, fullscreen
and with the secondary window, as smooth as `--software` (at most occasional minor stutter);
`--renderer=software` as before; `--renderer=banana` warned and ran as auto. The `D3D11`
device line was missing from the Log in every run: the Player subscribed to diagnostics
after binding the surface, which creates the device. Fixed in the Player (subscribe first).

Baseline: Direct3D11 created its device with `DriverType.Hardware` only, and nothing chose
another renderer. Without a usable GPU device, video did not show unless the host passed
`--software`; and the software renderer is the slowest way to fill a large window on Windows
(ENG-017: WPF's bitmap path dominates once our renderer is fast).

Delivered:

- Direct3D11: `Direct3D11PresentationOptions.DeviceSelection` = `Auto` (default: the GPU,
  then WARP, Windows' multi-core software rasterizer), `Hardware`, or `Warp`. WARP keeps the
  native swap-chain path, so overlays, DirectComposition and presentation are unchanged.
  Every device creation logs one diagnostic naming the kind, the adapter and the feature
  level; falling back to WARP is a warning quoting the GPU failure. A failure quotes both
  the GPU and the WARP result codes. The Microsoft Basic Render Driver (what Windows uses
  with no display driver installed: WARP behind a hardware driver type) is reported as a
  software rasterizer, not a GPU.
- Core: `IMediaPresentationAvailabilityProbe` (optional on a coordinator factory) returns
  `MediaPresentationAvailability`: available or not, a detail line, and what the renderer
  needs - `RequiresSoftwareDecoding`, `RequiresFrameSinkSurface`.
  `FallbackPresentationCoordinatorFactory` uses the first available candidate (no probe =
  available; none available = the last candidate), chooses once on first use, and keeps
  `Selection` (each probed candidate and why) and `SelectedAvailability`.
- Direct3D11's factory probes by creating and releasing a device with its selection; on a
  software rasterizer it asks for software decoding (a hardware decoder's frames carry their
  GPU device, which the renderer adopts). The software factory's probe always succeeds and
  asks for a frame-sink surface and software decoding.
- `WindowsDirect3D11MediaControlPlatformFactory.CreatePresentationFactory(options)` is the
  one place that wires Direct3D11 with the Windows precision waiter, for hosts building a
  chain.
- Player: `--renderer=auto|gpu|warp|software` (`--software` still works). `auto` (default)
  is Direct3D11 `Auto`, then the software renderer, resolved at startup because the surface
  mode and decoding follow the choice (off the UI thread; see Decisions): the selected
  renderer's `RequiresFrameSinkSurface` picks `WpfComposited`, `RequiresSoftwareDecoding`
  picks software decoding. Forced `warp`
  uses software decoding; forced tiers skip the startup probe. The status line names the
  renderer and device; the Log panel's `Renderer` entries show each probed candidate and
  why, written before the engine starts so they are there even if native loading fails.
- Tests: `FallbackPresentationCoordinatorFactoryTests` (8, Core, run in both test projects)
  and `Direct3D11DeviceSelectionTests` (3, Windows CI: WARP device creation, Auto always
  finds a device, default selection).

## Decisions made without the owner

- WARP is tried inside Direct3D11 rather than as a separate chain entry, so every
  Direct3D11 host (including `WindowsDirect3D11MediaControlPlatformFactory` users) gets it by
  default; the software renderer is only reached when no Direct3D11 device exists at all.
- The chain is resolved once, at startup; no switching renderers mid-session (a lost device
  recovers inside Direct3D11 as before).
- Auto costs one extra device creation at startup (the probe). It runs on a background
  thread started first thing in `App.OnStartup`, so the window appears without waiting; the
  view model awaits it just before creating the engine (which loads the native DLLs anyway).
  Until then the Player assumes the GPU path (or the software renderer for an explicit
  `--software`); if the chain lands on the software renderer, the view model switches the
  surface to `WpfComposited` before any media is loaded. Forced tiers skip the probe.
- A forced `Warp` with hardware decoding still adopts the decoder's GPU device for
  hardware frames (video keeps working) and logs that it left the software rasterizer.

## Manual smoke (Windows, native DLLs)

Pull first:

```
cd D:\development\FatzBomb\FatzMedia\Video
git pull
```

1. Default (`auto`): `dotnet run --project FatzBomb.MediaControl.Player -c Release`.
   Status line starts `Renderer: Direct3D11 (auto): GPU, <your adapter>, feature level ...`;
   Log panel shows `Renderer: Direct3D11: available ... (selected)` and a `D3D11` line naming
   the GPU device. Play video and CD+G; everything as before.
2. WARP: `dotnet run --project FatzBomb.MediaControl.Player -c Release -- --renderer=warp`.
   Status line `Direct3D11 WARP (forced)`; Log `D3D11: Direct3D11 device: WARP (Windows
   software rasterizer), Microsoft Basic Render Driver, ...`. Play video windowed, then
   fullscreen on the ultrawide, then with the secondary window; seek; play CD+G. Compare the
   smoothness with `--software` (expected: at least as good, with the native-window path).
3. Software: `-- --renderer=software` (and `-- --software`): as before (WpfComposited,
   software decoding).
4. GPU only: `-- --renderer=gpu`: as the default on a machine with a GPU.
5. Unknown value: `-- --renderer=banana`: Log warning naming the value; plays as `auto`.
