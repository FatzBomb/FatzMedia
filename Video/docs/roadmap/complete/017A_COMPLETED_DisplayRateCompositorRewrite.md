# Completed Stage 017A - Display-Rate Compositor and Presentation Scheduling

Prepared: 2026-10-16
Status: Completed 2026-10-18. Slices 1-5 (cadence model, cadence report, per-monitor refresh
rate, opt-in display-locked selection, per-display cadence); owner smoke passed 2026-10-18
(`updates/017A_1` to `updates/017A_5`). Possible later work moved to
`future/041A_IDEA_EngineInternalFollowUps.md`.

## Cadence Model (pinned)

At each display refresh `v` of a target with rate `D`, the visible source frame is the
latest one whose presentation time the media clock has reached:
`frame(v) = floor((v / D + phase) * S)` for source rate `S`. Rates are exact rationals
(23.976 = 24000/1001, 59.94 = 60000/1001) and arithmetic is exact, so hour-long runs
have no floating-point drift. The media (audio) clock stays authoritative; the model only
decides which already-decoded frame each refresh shows. Every target has its own cadence.

## Specification Table (verified by `DisplayCadenceTests`)

| Source | Display | Holds | Drops | Notes |
|---|---|---|---|---|
| 24 | 60 | 3,2 alternating | 0 | classic 3:2 pulldown |
| 23.976 | 59.94 | 3,2 exact | 0 | broadcast-matched |
| 23.976 | 60 | 3,2 | 0 | ~4 broken pairs per minute (0.1 % drift) |
| 25 | 50 / 30 on 60 / 60 on 120 | constant 2 | 0 | judder-free |
| 24 | 120 | constant 5 | 0 | judder-free (preferred projector rate) |
| 25 | 60 | 2/3 mix, mean 2.4 | 0 | inherent judder |
| 29.97 | 60 | 2, one 3 every 500 frames (~16.7 s) | 0 | |
| 30 | 59.94 | 2, one 1 every 1000 refreshes (~16.7 s) | 0 | display still faster: no drops |
| 60 | 50 | 1 | 1 in 6 | source faster than display: drops |

Phase (media-clock offset against vsync) only shifts which refresh shows each new frame.

## Slice 2 - Cadence report (2026-10-17)

`CadenceAdvice` describes how the open video's frame rate fits the display refresh rate
(smooth, 3:2 pulldown, occasional long hold, uneven mix, or skipped frames) and lists the
common refresh rates that would be smooth. `VideoPipeline` raises it as an Info diagnostic
(`VideoCadence`) once per open. Report only.

## Slice 3 - Per-monitor refresh rate (2026-10-18)

`IMediaRendererBackend.GetSurfaceRefreshRateHz` reports the rate of the display showing a
surface. Direct3D11 reads the window's monitor rate exactly from `QueryDisplayConfig`,
cached and refreshed in the background. Video timing resolves host override > renderer
(primary monitor) > platform (DWM) > 60 Hz, and the cadence report names the source.
Vulkan/software return null until they present to windows.

## Slice 4 - Display-locked frame selection (2026-10-18)

`VideoFrameTiming.DisplayLocked` (public; `MediaControlPlayer.VideoFrameTiming`,
`IMediaControlSession.VideoFrameTiming`; default `SourceCadence`, unchanged):

- The renderer reports each surface's display timing:
  `IMediaRendererBackend.TryGetSurfaceVsyncTiming` returns a `PresentationVsyncTiming`
  (display key, last vertical blank, period).
  - Direct3D11 keeps one vblank clock per monitor (`MonitorVblankClocks`). A background thread
    waits on the monitor's own blanks (`IDXGIOutput.WaitForVBlank`) and keeps a phase-locked
    grid (`VblankGrid`).
  - The thread runs only while timing is asked for and stops after 3 s without a query.
- The presentation loop wakes a measured budget (2 to 12 ms, under 60 % of a refresh) before
  the next refresh and presents the frame for that refresh.
- The media time at the refresh comes from the audio clock projected to the refresh and passed
  through `VsyncMediaClock`. This is an alpha-beta filter on the refresh grid:
  - it averages the clock's read jitter;
  - it follows drift and tempo;
  - it restarts on seeks, loops and pauses.
- Selection is the cadence model with a half-refresh phase (`DisplayLockedTiming`): each frame
  starts on the refresh nearest its timestamp.
- `CadencePhaseLock` adds hysteresis for cadences with up to 4 phase families (2.5, 2, 3, 5
  refreshes per frame), so a drift crossing breaks the cadence once instead of flip-flopping.
- With no current timing (software output, WPF-composited surface, timing not measured yet),
  each pass falls back to the source-cadence path.
- The Log panel (`VideoFrameTiming`) reports when the mode turns on or is unavailable and why.
  Every 60 s, and on pause, it gives the refreshes-per-frame histogram, late presents, drift
  steps and clock restarts.
- Player: **Display-Locked** check box in the A/V flyout, off by default.

## Slice 5 - Each display its own cadence (2026-10-18)

With display-locked timing and surfaces on monitors with different refresh rates:
- The coordinator groups the attached surfaces by display (`PresentationTimingGroup`). A
  surface the renderer has no timing for follows the primary's display.
- The loop serves whichever display's refresh comes next, with that display's own filtered
  clock, phase lock, work budget and statistics.
- Decoded frames stay shared and only move forward on the one media clock. A display whose
  refresh reaches the current frame later shows it then (the frame is raised again for that
  display).
- A frame raised for some displays carries their targets (`VideoFrameEventArgs.Targets`,
  internal). The coordinator composes for those targets only (`MediaCompositionFrame.Targets`,
  a `MediaCompositionTargetFilter`). Direct3D11 and the software renderer honour it; Vulkan
  presents to every target, as before.
- CD+G, subtitle, background and retained refreshes are not filtered: they go to every target
  as before.
- With one display (or all surfaces on one monitor) nothing is filtered; slice 4's behaviour.

## Next Slices

None planned. The owner's smoke settled the default: source cadence, display-locked opt-in.
Possible later work:
- display-locked as the default for Direct3D11 native windows, if later smokes show it ahead;
- vertical-blank timing for WPF-composited and Vulkan surfaces.

## Explicit Non-Goals

As in the original idea: no decoder/demuxer/clock rewrite, no renderer vblank model in
Core, no assumption of equal target refresh rates.
