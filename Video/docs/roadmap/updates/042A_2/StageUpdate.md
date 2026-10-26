# Stage Update

Stage: 042A.2 - HDR output on HDR displays (Direct3D11, opt-in)
Date: 2026-10-26 (plan and policy groundwork: 2026-10-24)

## Status: IMPLEMENTED - pending manual smoke

Owner request: stage 042A, slice 2: present HDR video as HDR when Windows HDR is on for the
monitor, instead of tone mapping it to SDR. Opt-in, default off.

The 2026-10-24 groundwork (renderer-neutral policy and math, `HdrOutputPolicy`) waited for
ENG-026, which changed the same swap-chain, resize and render-target code. ENG-026's resolved
items have landed (`ENGINEERING_ISSUES.md`); its open items are measurements (frame-sink resize
churn, shared composition of same-size targets, small allocations, Vulkan readback) and touch
nothing this slice changes. Nothing blocked the wiring.

## What the wiring does

With `Direct3D11PresentationOptions.HdrOutput = true`, before each frame is drawn the renderer
decides the primary window's output with `HdrOutputPolicy` and switches its swap chain:

- **scRGB** when the frame is hardware-decoded HDR10 (PQ) or HLG video, the monitor showing the
  window has Windows HDR on, and nothing below rules it out. The swap chain becomes
  `R16G16B16A16_FLOAT` with `SetColorSpace1(RGB_FULL_G10_NONE_P709)` (1.0 = 80 nits) and HDR10
  metadata (`SetHDRMetaData`).
- **SDR** otherwise: `B8G8R8A8_UNORM`, `RGB_FULL_G22_NONE_P709`, HDR video tone mapped exactly as
  in 038A and 042A slice 1.

A steady state costs nothing per frame: the decision is cached by its inputs, the swap chain only
changes when the mode does (`ResizeBuffers` with the new format, at the current size, after every
back-buffer reference is released as for a resize), and the metadata only when it changes.

### Pieces (all in `FatzBomb.MediaControl.Rendering.Direct3D11` unless noted)

- **Option**: `Direct3D11PresentationOptions.HdrOutput` (bool, default false). Off, the renderer
  never queries Windows for HDR state and every swap chain stays SDR.
- **Core** (`Core/Video/Hdr/HdrOutputPolicy.cs`): the policy types are now public, as a renderer
  contract like `RendererDiagnosticLatch` (`HdrDisplayInfo`, `HdrOutputMode`,
  `HdrOutputDecision`, `Hdr10MetadataValues`, `HdrOutputPolicy`). New:
  `HdrOutputPolicy.Restrict(decision, blocker)` turns an scRGB decision into SDR for a reason the
  policy does not see. No Win32, DXGI or shader code in Core.
- **Detection** (`Presentation/Hdr/Win32MonitorHdr.cs`): the DXGI output whose monitor is the
  window's (`MonitorFromWindow`, `IDXGIOutput6.GetDesc1`: HDR on when its color space is
  `RGB_FULL_G2084_NONE_P2020`, `MaxLuminance` as the peak) and the SDR white level
  (`QueryDisplayConfig` + `DisplayConfigGetDeviceInfo(DISPLAYCONFIG_SDR_WHITE_LEVEL)`, value /
  1000 * 80 nits). The DXGI factory is created again when `IsCurrent()` is false (DXGI caches
  outputs per factory, so an HDR toggle would otherwise go unseen).
- **Cache**: `MonitorQueryCache<T>`, the 017A refresh-rate cache made generic (its own commit):
  primed when the primary window attaches (host thread), refreshed in the background once a
  second, so moving the window to another monitor or toggling HDR in Windows Settings is seen
  within about a second and the presentation thread never queries Windows.
- **Controller** (`Presentation/Hdr/D3D11HdrOutputController.cs`): per frame, for the primary
  backend: decide, switch, report. One `D3D11.HdrOutput` line per change of mode or reason
  (`scRGB (the monitor is in HDR mode (peak 600 nits, SDR white 200 nits)).`,
  `SDR (the monitor is not in HDR mode).`); Info, or Warning when a failure is the reason.
- **Swap chains** (`Presentation/Hdr/D3D11HdrSwapChain.cs`, `ID3D11HdrOutputBackend`): the
  DirectComposition base swap chain and the native HWND swap chain switch format and color space;
  resizes keep the format (`Format.Unknown`) and re-set the scRGB color space. The
  DirectComposition overlay swap chain (the CD+G split) always stays SDR: DWM composes it at the
  SDR white level over the scRGB base.
- **Video** (`D3D11HdrToneMapPresenter`): the 038A/042A shader with one more constant
  (`Output.w`): for scRGB it maps PQ through the BT.2390 curve with the display's peak as target
  (compresses only content brighter than the display) and HLG through the BT.2100 OOTF for the
  display's peak (no curve), converts BT.2020 to BT.709 unclipped and writes nits / 80. Constants:
  `D3D11HdrToneMapPresenter.ForScRgbOutput` / `CreateConstants(..., hdrOutput)`.
- **Composition** (`D3D11MediaCompositionPresenter`): on an scRGB target the HDR video background
  is an FP16 layer filled by that shader; every SDR layer (clear color, wash, CD+G, subtitles,
  image backgrounds) goes through the sRGB EOTF times SDR white / 80 in the pixel shader
  (`OutputTransform` constant), unpremultiplied first for premultiplied layers, and blends in
  linear light. An FP16 background drawn into an SDR target (a retained frame after the output
  went back to SDR) is encoded back to sRGB with SDR white at 1.0, so it is never black or
  washed out. On SDR targets every shader returns exactly what it did before.
- **Both video paths**: plain hardware video (`PresentNativeFrame`, primary only) and composition
  (subtitles, CD+G, washes) decide the same way, so a subtitle cue appearing does not switch the
  swap chain.

### What stays SDR (decision reasons)

| Case | Reason in `D3D11.HdrOutput` |
| --- | --- |
| Option off | (no line; nothing changes) |
| SDR video, CD+G/MIDI without video, image backgrounds | `the video is SDR` |
| Monitor without Windows HDR | `the monitor is not in HDR mode` |
| A secondary or projector window, or an offscreen secondary, is attached | `another window shows the video (secondary windows are SDR)` |
| A video effect (`BaseVisualTransform`) is on | `a video effect is on (effects are SDR)` |
| Primary is offscreen: WPF composited (managed frame sink), Avalonia shared textures | `this surface shows SDR only (WPF composited or shared-texture mode)` |
| Swap chain cannot switch (legacy Discard chain, no `IDXGISwapChain3`, `ResizeBuffers` or `SetColorSpace1` failed, color space unsupported) | the failure, as a Warning |
| Software decoding, `--hdr-tonemap-cpu`, `--no-hdr-tonemap` | `the video is SDR` (frames arrive as tone-mapped BGRA, or untagged) |

## Fallback (never a black screen)

- **Swap-chain change fails**: `D3D11HdrSwapChain.TryApply` reverts to `B8G8R8A8` +
  `G22_P709` when the format changed but the color space could not be set; the backend
  remembers the failure (`HdrOutputFailure`) and the controller keeps that swap chain SDR,
  reported once as a Warning. Retried only when the swap chain is created again (new surface,
  device-loss recovery). What the compositor and shader draw always follows the target's
  actual format (`HdrOutputMode` read from the back buffer), never the intent.
- **HDR shader fails on an scRGB target** (e.g. `d3dcompiler_47.dll` missing): the frame is not
  drawn, the decoder's CPU tone mapper takes over (as in 038A), its frames are SDR, and the
  next frame switches the swap chain back to SDR. The video processor is never asked to fill
  an FP16 target.
- **Device loss**: the swap chains are created again in SDR; the next frame switches again.
- **Monitor change, HDR toggled**: the cached display state is refreshed off-thread within about
  a second; the next frame's decision switches the swap chain (one `D3D11.HdrOutput` line).
  A paused frame is not re-presented by the direct video path: the scRGB swap chain keeps
  showing it (DWM maps it to the SDR monitor, clipped) until the next frame.

## Decisions made without the owner

- **The DirectComposition primary is wired, not only native HWND.** The plan named the native
  HWND backend "the Player's main path", but `NativeD3D11PresentationBridge.AttachSurface` tries
  DirectComposition first for every primary window (WPF, WinForms, Avalonia native host) and
  falls back to native HWND only when DirectComposition fails. Both share one switching helper.
- **scRGB, not HDR10 (PQ) swap chains**, as planned: linear blending of SDR layers, out-of-gamut
  colors kept.
- **SDR white**: the system's SDR white level for the monitor (Settings > Display > HDR > SDR
  content brightness), 80 nits when Windows does not report one. SDR layers follow the slider.
- **Metadata**: BT.2020 primaries, D65, MaxCLL and mastering peak = the brightest light the
  output holds (PQ: min(content peak, display peak); HLG: the display's peak), frame average 0.
- **One decision per composition**: with more than one target, everything is SDR (the HDR
  background layer is shared by all targets of a composition).
- **Video effects are SDR**: `ApplyVisualTransform` clamps to [0, 1]; HDR versions of the
  effects are not in this slice.

## Tests

Portable (run here): `HdrOutputPolicyTests.ABlocker_TurnsOnlyAnHdrDecisionIntoSdr`.

Windows CI only (`D3D11HdrOutputTests`, the `D3D11*` naming rule):
- Shader mirror: PQ grays (4000 on 600, 1000 on 1000, 1000 on 600 nits) and HLG grays (600,
  1000, 1500 nits) through `CreateConstants(..., hdrOutput)` and the `PSToneMap` formula match
  `HdrOutputPolicy.PqNitsToScRgb` / `HlgToScRgb`; HLG white is the display peak; a saturated
  BT.2020 red keeps a negative green (unclipped); the scRGB constants.
- Controller with a fake backend: off never queries or switches; HDR video on an HDR monitor
  switches once to scRGB and back for SDR video, with exactly two Info lines; metadata
  (400-nit grade 400, 4000-nit grade 600, HLG 600); SDR monitor and blockers keep SDR; a failed
  switch is tried once, falls back to SDR and raises one Warning; an offscreen primary stays SDR.
- Native layouts: `DXGI_HDR_METADATA_HDR10` 28 bytes, `DISPLAYCONFIG_SDR_WHITE_LEVEL` 24 bytes;
  `Win32MonitorHdr.TryGet(0)` is null.
- WARP: a composition swap chain switches to FP16 and back, or fails, says why and is left SDR;
  the compositor on an FP16 target puts a white clear at 2.5 (200-nit SDR white) and blends a
  50% red wash in linear light, while the same frame on a BGRA target is unchanged.

The pure tests among these (shader mirror, controller, layouts) were also run on Linux by
compiling the file into a scratch net10.0-windows test assembly: 43 passed. The two WARP tests
need Windows.

## Verification

- Linux: `EnableWindowsTargeting=true dotnet build FatzBomb.MediaControl.sln -c Release`: 0
  warnings, 0 errors. `dotnet format --verify-no-changes` clean. The four boundary scripts pass.
- Tests: Portable 1346, Linux 94 (with the native FFmpeg tone-mapping tests), Avalonia 33,
  Vulkan 67: all pass.
- Not verified: anything on Windows. No D3D11 device, no HDR monitor here. The WARP tests and
  every D3D11 path above are checked by reasoning only until Windows CI and the owner's smoke.

## Manual smoke (Windows)

Prerequisites: a host with `Direct3D11PresentationOptions.HdrOutput = true` (the Player's
`--hdr-output`, see "Host flag" below), native DLLs in place, hardware decoding on (default),
`C:\fbtest\hdr10.mp4` and `C:\fbtest\hlg.mp4`. Watch the Log for `D3D11.HdrOutput`.

To check the monitor: Settings > System > Display > select the monitor > "Use HDR" (Windows 11;
Windows 10: "Windows HD Color settings"). Only an HDR-capable monitor shows the switch. With it
on, the HDR settings page shows "SDR content brightness".

1. **SDR monitor, or "Use HDR" off** (anyone can run this): play both clips with `--hdr-output`.
   - Correct: they look exactly as without the flag (042A slice 1 tone mapping). The Log shows
     `D3D11.HdrOutput: SDR (the monitor is not in HDR mode).` once.
   - Wrong: any color or brightness change against a run without the flag; a black frame.
2. **Regression, flag off**: play both clips, an SDR clip, a CD+G file and a subtitled video
   without `--hdr-output`. Everything looks as before; no `D3D11.HdrOutput` line at all.
3. **HDR monitor, "Use HDR" on, `--hdr-output`**: play `hdr10.mp4`.
   - Log: `scRGB (the monitor is in HDR mode (peak N nits, SDR white M nits)).`
   - Correct: highlights (sun, lamps, specular glints) clearly brighter than the white of the
     Player's own UI and other windows, with detail in them; skin tones and mid grays about as
     bright as before; colors saturated, not pastel.
   - Wrong: everything washed out or gray (scRGB values read as sRGB), everything dark with
     crushed shadows (sRGB values read as linear), highlights flat at UI white (still tone
     mapped), or a black window.
   - Toggle the flag off and compare: without it the clip is the tone-mapped SDR picture
     (highlights at UI white).
4. Same, `hlg.mp4`: highlights brighter than UI white, mid tones natural, no gray veil. The Log
   line is the same scRGB line.
5. **SDR layers on HDR**, still on the HDR monitor: play the HDR clip with subtitles (an .srt
   beside it) and a CD+G file with an HDR video background if you have one.
   - Correct: subtitle text and CD+G graphics are as bright as other SDR apps (Notepad's white),
     not glowing and not dim. Move Settings > HDR > "SDR content brightness": after about a
     second the subtitles follow it; the video's highlights do not.
   - A subtitle cue appearing or disappearing must not flash or change the video's brightness
     (no swap-chain switch per cue; one `D3D11.HdrOutput` line for the whole clip).
6. **Monitor and setting changes**:
   - Toggle "Use HDR" off while the HDR clip plays: within about a second the Log shows
     `SDR (the monitor is not in HDR mode).` and the picture becomes the tone-mapped SDR one;
     toggle it on again: back to scRGB. No lasting black frame, no error. (Windows itself blanks
     the screen for a moment when HDR is toggled; that is not this slice.)
   - Two monitors, one HDR and one SDR: drag the playing window between them. One
     `D3D11.HdrOutput` line per move; HDR on the HDR monitor, tone-mapped SDR on the other.
   - Resize and maximize the window during HDR playback: stays HDR, no black flashes.
7. **Things that must stay SDR** (HDR monitor, flag on), each with its Log reason:
   - Open the projector/second-screen window: `another window shows the video ...`; both windows
     SDR tone mapped; close it: back to scRGB.
   - Turn on a video effect (hue, saturation, gain): `a video effect is on ...`; off: scRGB.
   - `--hdr-tonemap-cpu` or software decoding: SDR, `the video is SDR`.
   - WPF composited mode, and the Avalonia player (`--renderer=direct3d11`, shared textures):
     `this surface shows SDR only ...`, tone mapped as in slice 1.
8. **Device loss** (optional): with the HDR clip playing, `dxcap -forcetdr` or a driver update:
   playback recovers, first in SDR, then scRGB on the next frame.

### Host flag

A host passes `new Direct3D11PresentationOptions { HdrOutput = true }` to
`WindowsDirect3D11MediaControlPlatformFactory` / `CreatePresentationFactory`. The WPF Player
takes `--hdr-output` (`App.HdrOutput`, passed to `PlayerRendererSelection.Resolve`). The WinForms
player can take the same flag; the Avalonia player's Direct3D11 path uses shared textures and
would only log the SDR reason.

## Not in this slice

- HDR on secondary/projector windows and offscreen secondaries (one composition would need an
  SDR and an scRGB background layer).
- HDR versions of the video effects (`BaseVisualTransform`).
- HDR for software-decoded HDR video (frames reach the renderer tone mapped as BGRA).
- WPF composited mode and the Avalonia GPU-shared surface (8-bit BGRA by design; see the plan).
- Re-trying a swap chain whose switch failed before it is created again.
- HDR10 (PQ) swap chains for exclusive full screen; Dolby Vision; dynamic metadata.
