# Stage Update

Stage: 042A.2 - HDR output on HDR displays (Direct3D11): plan and policy groundwork
Date: 2026-10-24

## Status: PLANNED - policy and math implemented and tested; Direct3D11 wiring not started

Owner request: stage 042A, slice 2 "if time allows; otherwise write the plan". This update
contains the plan, plus its renderer-neutral pieces, which can be tested here. It does not
change any output: nothing calls the new policy yet, and SDR output (with 038A/042A slice 1
tone mapping) is what every renderer presents.

## Why the wiring is not in this slice

- Every Direct3D11 presentation and composition texture is `B8G8R8A8_UNorm` sRGB: the video
  processor output, composition layers, the DirectComposition, native-HWND and shared-texture
  backends, and the managed frame sink. HDR output needs an FP16 path through the composition
  presenter (video in scRGB, SDR layers linearized and scaled), not just another swap-chain
  format.
- It changes the swap-chain creation, resize and render-target code that ENG-026 (another
  session) is changing right now. Doing both at once would conflict.
- It cannot be checked here (no Windows, no HDR monitor). It is better built as its own slice
  right after ENG-026 lands, with the owner's smoke after each backend.

## Delivered (groundwork)

**Core** (`Core/Video/Hdr/HdrOutputPolicy.cs`, all internal)
- `HdrDisplayInfo`: what a monitor reports (HDR on, peak nits, SDR white nits).
- `HdrOutputMode`: `Sdr` or `ScRgb`.
- `HdrOutputDecision`: the mode, the display peak, the SDR white level and a reason for the
  diagnostic.
- `HdrOutputPolicy`:
  - `Choose(enabled, content transfer, display)`:
    - SDR unless the option is on, the frame is HDR (PQ or HLG) and the monitor is in HDR
      mode.
    - An unreported or implausible peak is taken as 1000 nits. An unreported SDR white is taken
      as 80 nits (Windows' default).
  - `NeedsSwapChainChange`: only a mode change (SDR and HDR monitors) rebuilds the swap chain.
    A change of peak or SDR white updates constants only.
  - `SdrWhiteLevelToNits`: `DISPLAYCONFIG_SDR_WHITE_LEVEL`, where 1000 is 80 nits.
  - `SdrScale` and `SdrToScRgb`: SDR layers through the sRGB EOTF, times SDR white / 80.
  - `PqNitsToScRgb`: the BT.2390 curve with the display's peak as target, which compresses
    only content brighter than the display, divided by 80.
  - `HlgToScRgb`: the BT.2100 OOTF for the display's own peak (`HlgCurve.SystemGamma` adapts
    gamma), no curve.
  - `CreateMetadata`: `DXGI_HDR_METADATA_HDR10` values (BT.2020 primaries, D65 white,
    chromaticities in 0.00002 units, minimum luminance in 0.0001 nits).

**Tests (`HdrOutputPolicyTests`, Portable, 15 cases)**
- The decision table.
- Fallbacks for unreported values.
- Switching between monitors.
- SDR white level conversion (80, 200, 480 nits).
- SDR layers: black, white at 80 and 200 nits, sRGB mid gray, every level rising.
- PQ: unchanged on a bright enough display, compressed to a 600-nit display's peak, shadows
  kept.
- HLG: white at the display peak, 75% with the display's gamma (1.107 at 600 nits).
- Metadata encoding.

## Decisions made without the owner

- **scRGB (FP16), not an HDR10 (R10G10B10A2, PQ) swap chain.**
  - SDR layers blend in linear light, scaled by the SDR white level, and DWM composes in scRGB
    anyway.
  - Out-of-gamut BT.2020 colors survive as negative or above-1 values.
  - HDR10 output could be added later for exclusive full-screen if the owner sees a need.
- **HDR output only for HDR video on an HDR monitor.** SDR video on an HDR monitor stays on the
  SDR swap chain, and Windows maps it at the SDR content brightness, as it does today.
- **HLG on an HDR display** uses the BT.2100 OOTF for that display's peak, not the 1000-nit
  nominal one.
- **Avalonia GPU-shared surface (027A slices 2 and 5) stays SDR, tone mapped.**
  - Avalonia 12.1's compositor imports external GPU images only as `B8G8R8A8UNorm` or
    `R8G8B8A8UNorm` (`PlatformGraphicsExternalImageFormat`).
  - It has no API to put its own swap chain into an HDR color space.
  - So a shared FP16 or PQ image could not be shown as HDR; Avalonia would clip or misread it.
  - The renderer keeps sending tone-mapped SDR to Avalonia surfaces. Revisit when Avalonia
    exposes HDR output.

## Plan (next slice)

The plan is in `docs/roadmap/active/042A_ACTIVE_HdrFollowUps.md` (slice 2), in this order:
1. Per-monitor detection cache.
2. The native HWND backend with an FP16 composition path behind
   `Direct3D11PresentationOptions.HdrOutput` (default off) and Player `--hdr-output`.
3. The other backends.

## Verification

- Linux:
  - The full solution builds in Release with no warnings.
  - `dotnet format --verify-no-changes` is clean, and the boundary scripts pass.
- Tests pass: Portable 1206 (15 new), Linux 59, Avalonia 33, Vulkan 62.
- Nothing on Windows changes in this slice, so there is no manual smoke for it yet.

## Manual smoke (Windows) - for the wiring slice, once built

1. Windows HDR off (or an SDR monitor), `--hdr-output`:
   - Play an HLG clip and an HDR10 clip. They are tone mapped exactly as in 042A slice 1.
   - The Log shows `D3D11.HdrOutput: SDR (the monitor is not in HDR mode)`.
2. Windows HDR on (Settings > Display > Use HDR), `--hdr-output`, with the same two clips:
   - The Log shows `ScRgb (the monitor is in HDR mode, peak N nits, SDR white M nits)`.
   - Highlights are brighter than SDR white, not clipped, and the picture is not washed out.
   - CD+G, subtitles and backgrounds are as bright as other SDR apps. Move Settings >
     Display > HDR > "SDR content brightness" and check that they follow it.
3. Two monitors, one HDR and one SDR. Move the playing window between them:
   - The Log shows one `D3D11.HdrOutput` line per move.
   - The picture switches between HDR and tone-mapped SDR, with no lasting black frame,
     flicker or error.
4. Without `--hdr-output`, with HDR on: the same picture as 042A slice 1 (tone mapped SDR).
5. The Avalonia player on Windows (`--renderer=direct3d11`) with HDR on: SDR tone mapped, as
   decided above.
