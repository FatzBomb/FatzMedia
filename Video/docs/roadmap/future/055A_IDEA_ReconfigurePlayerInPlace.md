# Stage 055A (planned) - Reconfigure a player in place

Owner decision 2026-11-05. Queue item 6.

**Already there (043A slice 2):** `SetModeAsync(MediaPlayerMode)` switches a player's mode on the fly,
reopening the open media at its position and in its transport state, and raises `ModeChanged`.

**This stage extends that to the rest of the configuration:** `player.ReconfigureAsync(MediaPlayerConfiguration)`
on the same player object (events, references and attached surfaces stay valid) for the settings
`SetModeAsync` does not cover: audio engine (BASS or FatzAudio), renderer, audio device, normalization
options, logging. It reuses `SetModeAsync`'s reopen-at-position path, restores volume, `Normalize`,
loop, tempo and pitch where the new configuration allows, and raises `Reconfigured`; media the new
configuration cannot play stays loaded and reports why.

- The test players' `PlayerSetController` (already shared, ENG-039 batch 4a) uses it for renderer and
  audio-engine changes; player-count changes still create and dispose players.
- First step: a read-only look at how `SetModeAsync` rebuilds the service graph and what else holds
  engine references, reported before code.

## Slice 1: renderer fallback in place (owner decision 2026-11-05)

Moves out of the test players what belongs in the package (ENG-050 follow-up). Today each test
player's window catches `MediaError` with `RendererDeviceLost`, picks the next renderer
(`PlayerRendererRules.NextAvailableAfterDeviceLoss`) and rebuilds its players empty.

1. **Renderer swap in place:** the renderer part of `ReconfigureAsync` first. Same player object,
   events and attached surfaces; the media reopens at its position and transport state
   (`SetModeAsync`'s path).
2. **Automatic fallback after an unrecoverable device loss:** the creation-time chain from 036A
   (`FallbackPresentationCoordinatorFactory`: GPU, WARP, software) continues at run time. When
   recovery fails (or the renderer reports `CanRetryDeviceRecovery = false`), the player swaps to the
   next tier itself (Vulkan to Direct3D11 on Windows, Direct3D11 to WARP, WARP to Software; Software
   elsewhere), with a Warning and an event naming the old and new renderer (proposed
   `RendererChanged`). `RendererDeviceLost` stays as the error when no tier is left, or when the host
   turns the automatic fallback off (an option, on by default; name decided in the stage).
3. **Surface and decoding paired with the renderer by the package** (test-player audit 2026-11-05):
   Core reports `RequiresFrameSinkSurface` and `RequiresSoftwareDecoding` per renderer, but only the
   036A fallback chain reads them; the players pair them by hand (`Player/ViewModels/MainWindowViewModel.cs`,
   `Player/App.xaml.cs`, `PlayerRendererRules.DescribeDecoding`, `Player.Avalonia/Renderers.cs`, which
   also ignores Vulkan). The WinForms and Avalonia surfaces already fall back to a frame sink on their
   own; the WPF `MediaSurface` should pick composited mode itself when the renderer needs a frame sink,
   and Core should pick software decoding from the chosen renderer's availability.
4. **`Auto` renderer choice in the package:** the ranking each test player does today
   (`Renderers.Probe`/`Choose` in the Avalonia player, `PlayerRendererSelection` in WPF,
   `PlayerRendererRules` in Player.Shared): best available for the surface type (Vulkan, Direct3D11,
   WARP, Software as each platform allows), using the factories' existing `ProbeAvailability`.

The test players then drop their own fallback (`FallBackRenderer`, the controller's
`RendererDeviceLost` hook) and their ranking code, and show the event. The device-loss article's
"recreate the player" pattern becomes the manual alternative. Player-set layout, full screen, keys
and the renderer drop-down stay in the test players (app UI).

Smoke: as ENG-050's (Avalonia on Vulkan, `dxcap -forcetdr`): the same video keeps playing on
Direct3D11 at its position after the driver's reset (about 15 s on NVIDIA), without a rebuild.

Remaining slices: the audio engine, audio device, normalization and logging parts of
`ReconfigureAsync`, as above.
