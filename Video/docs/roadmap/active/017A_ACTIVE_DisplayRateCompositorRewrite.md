# Active Stage 017A - Display-Rate Compositor and Presentation Scheduling

Prepared: 2026-10-16
Status: Slice 1 (deterministic cadence model and specification), slice 2 (cadence report in
the host log, `updates/017A_2`), and slice 3 (per-monitor refresh rate from the renderer,
`updates/017A_3`) implemented. Frame selection at present time is not started (see Next
Slices).

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

## Next Slices

1. Use the model to choose frames at present time behind an opt-in option, with the
   current source-cadence path as the default and fallback.
2. Multi-target: each target runs its own cadence from the same composition plan; CDG and
   subtitle layers keep updating independently.

## Explicit Non-Goals

As in the original idea: no decoder/demuxer/clock rewrite, no renderer vblank model in
Core, no assumption of equal target refresh rates.
