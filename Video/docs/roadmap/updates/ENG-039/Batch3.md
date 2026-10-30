# ENG-039 batch 3: dead code and public API

2026-10-28. Branch `claude/festive-newton-yvvib5-b3` (on top of batch 2). Status: implemented.
No behaviour changes, so no manual smoke beyond the usual player start.

## Public API (decisions D-2, D-3, D-4 in `AuditPlan.md`)

The package is `0.1.0-alpha` and no caller exists for these, so they go now rather than through
`[Obsolete]` first (D-2):
- `MediaControlPlayer.Controls` and the `MediaControls` class are internal. `IMediaControlSession`
  (and the adapters' `Player`) is the control surface; tests reach the class through
  `InternalsVisibleTo`. `IsScrubbing` is no longer readable from outside; a host that calls
  `BeginScrub`/`EndScrub` already knows it, so no session member was added.
- `MediaControlPlayer.Video`, `.Audio`, `.Cdg` and `.Subtitle` are removed (no callers).
- `MediaFile` is internal and its 12 public `Load`/`LoadAsync` wrappers are removed; the player
  calls the internal overloads. `MediaControlPlayer.AutoBackground` replaces
  `MediaFile.BackgroundAutoDetect` in the docs.
- `IMediaRenderCoordinator.RunOnRenderThread` is removed (every implementation ran the action
  inline).
- The host-render-event mode is removed: `IVideoEngine.SetHostRenderEventDrivenPresentation`
  was only ever called with `false`, so `VideoPipeline` always runs its presentation loop.
- `SharpArchiveAdapter.IsArchive` is removed (`FileTypes.IsArchive` is the one used).

Kept with a warning for one release:
- WPF `MediaSurface.ShowSecondaryOutput`, `CloseSecondaryOutput`, `IsSecondaryOutputOpen` and
  `SecondaryOutputsChanged` are `[Obsolete]` (D-3): open a `SecondaryPresentationWindow` with
  `ShowOnScreen`, as the test players do. The surface's own mode-change reopen uses a private
  path.
- `ICdgEngine.LoadPaired(..., audioStreamHandle)` is `[Obsolete]` (D-4): the handle is BASS's and
  was never used. `CdgPipeline` calls `Load`.
- `MidiLyricExtractor` is an `[Obsolete]` forwarder (D-4) to the internal `MidiLyricCueBuilder`,
  which the player's `BassMidiLyricProvider` uses. Both go with BASS (037A).

## Internal dead code

- `VideoDecoder`: the transfer and buffer diagnostic timers (never reported).
- Vulkan: `VulkanOffscreenCompositor.LastVideoFailure`, `VulkanExportedImageSet.HasSemaphores`,
  the test-only `GetExportCapabilities(bool, bool)` overload (tests use `VulkanExportSupport`).
- Audio: the `IAudioFilter` interface and `BassEqualizerFilter`'s unused `Id`,
  `LatencySeconds`, `IsActive` and `Remove()`.
- Direct3D11: three unused `IsReady` properties, the presentation services' two snapshot
  pass-throughs, and `D3D11DeviceLostClassifier.IsDeviceLost(IntPtr)` (the decode side uses the
  Windows platform's own check).

## Verification

Release build (warnings as errors), the Portable, Linux, Vulkan and Avalonia test suites,
`dotnet format --verify-no-changes` and the four boundary scripts pass on Linux. Windows CI runs
the full suite. Manual: start the WPF player, play a video and a CD+G file, and toggle the
Secondary Window box (it uses `SecondaryPresentationWindow`, not the obsolete calls).
