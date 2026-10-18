# Stage Update

Stage: 017A.3 - Per-monitor refresh rate from the renderer
Date: 2026-10-18

## Status: IMPLEMENTED - manual Player smoke pending

Delivered:

- `IMediaRendererBackend.GetSurfaceRefreshRateHz(surface)` (default: null): the refresh rate
  of the display showing a surface. Called from presentation threads, so backends cache it.
- Direct3D11: `Win32MonitorRefreshRate` finds the window's monitor (`MonitorFromWindow`,
  `GetMonitorInfoW`) and reads that monitor's exact refresh rate from `QueryDisplayConfig`
  (a fraction, so 59.94 Hz is 60000/1001). `MonitorRefreshRateCache` primes it when a new primary
  native window is attached and refreshes it in the background at most once a second, so
  moving the Player to another monitor is picked up. A duplicated (cloned) desktop whose
  monitors run at different rates reports nothing, so the platform rate applies. Managed frame-sink (WPF composited)
  surfaces have no monitor and return null.
- Core: `MediaPresentationCoordinator` tracks the primary surface and hands the video
  pipeline a cheap source for its rate (internal `IRendererRefreshRateSink`; no public engine
  API change). `VideoPipeline` now resolves the display rate as: host
  `SetDisplayRefreshRate` > renderer (primary monitor) > platform (DWM's global rate on
  Windows) > 60 Hz. The presentation interval, presentation lead, and the 017A cadence report
  all use it.
- The cadence report is raised when playback first starts after an open (it was raised at
  open, before the coordinator had the primary surface), and says "of the monitor showing
  the video" when the renderer supplied the rate.
- Tests: coordinator wiring (shared, runs on Linux), cache freshness/background refresh/
  failure/removal and the Win32 struct layouts (Windows test project). Struct sizes were also
  checked here by loading the built assembly: 72/64/20/84/104 bytes, matching Win32.

## Decisions made without the owner

- `QueryDisplayConfig` over `EnumDisplaySettings`: the latter reports 59 for a 59.94 Hz mode.
- Vulkan and the software renderer keep the default (null); they have no window today.
- Only the primary surface drives video timing; a secondary window on another monitor still
  shows frames at the primary's cadence (multi-target cadence is the next 017A slice).

## Manual smoke (Windows, native DLLs)

1. Single monitor: play a 23.976/24 fps film. The `VideoCadence` line ends with "(Display rate
   of the monitor showing the video.)" and names your monitor's real rate (for example
   59.94 Hz rather than 60 if Windows is set to 59.94).
2. Two monitors with different refresh rates (if available): start the Player on one, open a
   film and play: the line names that monitor's rate. Close the film, drag the Player to the
   other monitor, wait a second, open and play again: the line names the other rate.
3. Playback looks the same as before (no new stutter); seeking and looping still work.

## Review follow-up (same day, local code review)

Fixed:
- Re-attaching a known window (surface recreation can run on the presentation thread after a
  device adoption or device-lost recovery) no longer queries Windows: `PrimeIfMissing`.
- Only the primary is primed; secondaries are not read yet.
- Detaching an old primary after a new one was attached no longer clears the new one.
- Cloned displays with different rates report nothing instead of the first path's rate.
- The cadence report is also raised for hosts that drive presentation from their render
  event (no presentation loop), and its pending flag is set before the pipeline reports open.
- The cache is cleared on dispose; the redundant active-path check is gone.

Not changed:
- The display rate is resolved several times per presentation-loop iteration (interval, lead,
  diagnostics). Each read is a lock and a dictionary lookup on the renderer's cache; a rate
  change landing mid-iteration affects that one iteration only.
- Resize does not re-query (it would run on every resize step of a drag); the background
  refresh picks up a move within a second.

