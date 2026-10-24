# Active Stage 042A - HDR Follow-Ups

Prepared: 2026-10-18 (idea, deferred from 038A). Activated: 2026-10-24.
Status: slice 1 (HLG tone mapping) implemented, pending manual smoke (`updates/042A_1`).
Slice 2 (HDR output on HDR displays, Direct3D11) is planned.

038A tone maps HDR10 (PQ) to SDR on the CPU for every renderer and in a Direct3D11 shader for
hardware-decoded video. This stage adds HLG to the same paths, then prepares real HDR output.

## Slice 1 - HLG tone mapping (implemented)

HLG (ARIB STD-B67, BT.2100) was shown as SDR without tone mapping, and the `Decoding ...` line
said so. HLG's signal is relative scene light, so it needs the BT.2100 OOTF: display light
depends on each pixel's scene luminance (system gamma 1.2 at 1000 nits). The 038A tables
indexed by the brightest channel's signal do not apply directly.

Acceptance criteria:
- A frame whose transfer (frame tag, else stream tag) is `arib-std-b67` is tone mapped whenever
  HDR10 would be (`HdrToneMappingOptions`, `--no-hdr-tonemap`, `--hdr-tonemap-cpu` unchanged).
- CPU path (`HdrToneMapper` with `HdrTransferFunction.Hlg`), per pixel:
  - inverse HLG OETF per channel to scene light E (BT.2100 Table 5);
  - scene luminance Ys with the BT.2020 weights;
  - the OOTF for the nominal peak Lw = 1000 nits, display light Lw * Ys^(gamma - 1) * E, with
    gamma = 1.2 + 0.42 log10(Lw / 1000) (BT.2100), so 1.2;
  - the existing BT.2390 curve on the brightest channel (source peak Lw, target SDR white),
    hue kept; then BT.2020 to BT.709 with clipping and gamma 2.4, as for PQ.
  - Table-driven like 038A: no `pow` or `exp` per pixel.
- Direct3D11: `PSToneMap` does the same math behind a transfer constant
  (`ToneMapConstants.Transfer`). The video-processor fallback declares
  `YCBCR_STUDIO_GHLG_TOPLEFT_P2020` for HLG.
- Software and Vulkan renderers (and Direct3D11 with `--hdr-tonemap-cpu` or a failed shader)
  get CPU-tone-mapped frames, so all renderers match.
- Diagnostics: `Decoding ...` says `HLG HDR, tone mapped to SDR`; one Info line per open:
  `HLG video is tone mapped to SDR on the CPU | by the renderer: display peak 1000 nits
  (BT.2100 nominal, system gamma 1.2), SDR white at 203 nits.` The `D3D11.Hdr` line names HLG.
- Tests mirror 038A: reference values (inverse OETF, system gamma, 75% HLG = 203 nits), monotonic
  and neutral grays over every 10-bit code, black and peak white, HLG and PQ reference white
  alike, BT.2020 red, the tables against the exact math, the shader formula against the CPU
  mapper (`D3D11HdrToneMapTests`), and the `Decoding ...` wording.
- Opt-in Linux native test (`FATZBOMB_LINUX_FFMPEG_DIR`) on the HEVC 10-bit HLG and HDR10
  clips through the software path: tone-map diagnostics, no white clipping, a plausible mean
  level, the red bar stays red, and the picture differs from `Disabled`.

Not in slice 1: HLG tagged with BT.709 primaries (rare; treated as BT.2020, like HDR10), a
configurable HLG nominal peak, Dolby Vision, dynamic metadata.

## Slice 2 - HDR output on HDR displays (planned)

When Windows HDR is on for the monitor, present HDR video as HDR instead of tone mapping it to
SDR. Opt-in: a Direct3D11 renderer option, default off, so SDR output is untouched.

Acceptance criteria (for the slice when it is built):
- `Direct3D11PresentationOptions.HdrOutput` (default false); Player `--hdr-output`.
- Per-monitor detection: `IDXGIOutput6.GetDesc1` for the output holding the window
  (`ColorSpace == RGB_FULL_G2084_NONE_P2020` means HDR is on; `MaxLuminance`,
  `MaxFullFrameLuminance`), and the SDR white level from `DisplayConfigGetDeviceInfo`
  (`DISPLAYCONFIG_SDR_WHITE_LEVEL`, value / 1000 * 80 nits). Checked again when the window
  moves to another monitor and on `DXGI_ERROR` / `IsCurrent() == false` of the factory.
- The pure decision (`HdrOutputPolicy.Choose`): SDR output unless the option is
  on, the frame is HDR (PQ or HLG) and the display is in HDR mode. HDR output is scRGB:
  `R16G16B16A16_FLOAT` swap chain, `SetColorSpace1(RGB_FULL_G10_NONE_P709)`, 1.0 = 80 nits.
- Video in HDR output:
  - PQ: nits from PQ, the BT.2390 curve with the display's peak as target (only compresses
    when the content is brighter than the display), BT.2020 to BT.709 without clipping
    (scRGB keeps out-of-gamut values), / 80.
  - HLG: the OOTF with Lw = the display's peak (BT.2100's gamma formula adapts to it), no curve.
- SDR layers (CD+G, subtitles, backgrounds, SDR video) are composed at the system SDR white
  level: sRGB EOTF, then * (SDR white / 80) (`HdrOutputPolicy.SdrScale`).
- `SetHDRMetaData` with HDR10 metadata: BT.2020 primaries and D65 in 0.00002 units, MaxCLL /
  mastering peak in nits, minimum in 0.0001 nits (`HdrOutputPolicy.CreateMetadata`).
- Moving the window between an SDR and an HDR monitor switches format and color space
  (`HdrOutputPolicy.NeedsSwapChainChange`); a peak or SDR-white change only updates constants.
- One `D3D11.HdrOutput` diagnostic per change names the decision and its reason.
- Tests (portable): the decision table, SDR white scaling, metadata encoding,
  switching. Shader formulas get a CPU mirror like `D3D11HdrToneMapTests`.

Plan for the Direct3D11 wiring (not started):
1. Native HWND backend first (`D3D11NativeHwndSwapChainBackend`, the Player's main path):
   swap-chain format and color space chosen per decision; resize keeps the format.
2. The composition presenter renders into an FP16 target when HDR output is on: video drawn
   by a variant of `PSToneMap` writing scRGB; every SDR layer through one linearize-and-scale
   pixel shader, blended in linear light.
3. Secondary windows, DirectComposition (WPF composited) and the shared-texture backend stay
   SDR in the first wiring slice (their decision is always SDR); they follow once the native
   path passes the owner's smoke.
4. This touches the swap-chain and render-target code ENG-026 is changing; start after ENG-026
   lands.

Avalonia GPU-shared surface (027A slices 2 and 5): **stays SDR (tone mapped).** Avalonia 12.1's
compositor imports only `B8G8R8A8UNorm` and `R8G8B8A8UNorm` external images
(`PlatformGraphicsExternalImageFormat`) and has no API to set an HDR color space on its swap
chain, so a shared FP16 or PQ image could not be shown as HDR. The renderer keeps tone mapping
for it; revisit when Avalonia exposes HDR output.

## Must Preserve

- 038A's SDR output, the CPU fallback, and identical results across renderers.
- PQ output is bit-for-bit unchanged by slice 1 (the PQ path of `HdrToneMapper` is untouched).
- Core boundary: no Direct3D, DXGI or shader code in Core.
