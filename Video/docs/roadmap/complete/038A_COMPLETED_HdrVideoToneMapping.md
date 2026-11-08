# Completed Stage 038A - HDR Video Tone Mapping

Prepared: 2026-10-18 (idea), activated 2026-10-18.
Status: Completed 2026-10-18. Slice 1 (HDR10 tone mapping on the CPU path, every renderer)
and slice 2 (Direct3D11 shader for hardware-decoded HDR10, with CPU fallback); owner smoke
passed 2026-10-18 (`updates/038A_1`, `updates/038A_2`: "everything looked good"). HLG and HDR
output moved to 042A (`active/042A_ACTIVE_HdrFollowUps.md`).

## Why This Is Worth Doing

ENG-019 makes the engine convert SDR video with the video's own color space. HDR video
(HDR10/PQ and HLG, usually 10-bit HEVC, AV1 or VP9 in BT.2020) is still shown as if it were
SDR, so it looks washed out and dim on every path. The `Decoding ...` log line already
flags these files. Many modern 4K releases are HDR, so the engine's own players and future
hosts hit this regularly.

## Recommended Direction

- **Core:** carry the transfer function (SDR gamma, PQ, HLG) in `VideoColorSpace` next to
  the matrix and range. Also carry HDR10 static metadata (mastering display, MaxCLL /
  MaxFALL) from FFmpeg's frame side data, so renderers can tone map consistently. Core
  decides the policy (tone map to SDR, or pass HDR through when the output supports it).
  The renderer performs it.
- **SDR output (first slice):** tone map to BT.709 SDR.
  - Direct3D11, hardware frames: render the decoded P010/NV12 texture with a YCbCr-to-RGB
    and tone-mapping pixel shader instead of the video processor. Drivers do not reliably
    tone map PQ to SDR in `VideoProcessorBlt`.
  - CPU path: swscale cannot tone map. Use FFmpeg's `zscale`/`tonemap` filters (if the
    shipped build has them) or a small Core tone-mapping step on 16-bit intermediate frames.
  - The curve is a fixed, documented operator (for example BT.2390 EETF or Hable) with
    a peak-brightness option. Keep the options small.
- **HDR output (later slice):** when the monitor is in Windows HDR mode, present
  through an FP16 or 10-bit swap chain with the matching DXGI color space
  (`IDXGISwapChain3.SetColorSpace1`) and HDR metadata, instead of tone mapping.
- **Vulkan and software renderers:** same policy, implemented in their own shaders or
  pixel paths, with parity tests against the Direct3D11 result.

## Recommended Immediate Action

1. Inventory the shipped FFmpeg 9 LGPL build: are `zscale`/`tonemap`/`libplacebo` present?
2. Collect two or three of the owner's HDR samples (HEVC 10-bit, AV1 10-bit) with their
   `Decoding ...` lines.
3. Prototype the Direct3D11 shader path on one sample and compare against mpv's default
   tone mapping.

## Slice 1 - HDR10 to SDR on the CPU path (implemented)

Decision: the engine's own tone mapper, not FFmpeg's `zscale`/`tonemap` filters. The shipped
FFmpeg 9 LGPL build has `zscale`, `tonemap` and `libplacebo`, but using them would tie the
result to each host's FFmpeg build, and the float `zscale` chain is slow at 4K. A table-driven
mapper in Core gives the same picture on every renderer and is testable without a GPU.

Acceptance criteria:
- `MediaPlayerConfiguration.HdrToneMapping` (`HdrToneMappingOptions`): on by default;
  `Disabled` shows HDR as before; `ReferenceWhiteNits` (default 203, BT.2408) is the SDR target
  the source's range is compressed into (the source peak becomes SDR white). Player: `--no-hdr-tonemap`.
- A frame whose transfer (frame tag, else stream tag) is PQ is tone mapped:
  - swscale converts it to full-range RGB48 with the BT.2020 matrix;
  - `HdrToneMapper` writes SDR BGRA: BT.2390 curve on the brightest channel (hue kept),
    BT.2020 to BT.709 primaries with clipping, BT.1886 gamma 2.4.
- The curve's source peak: MaxCLL, else the mastering display's peak (frame side data, else
  stream side data), else 1000 nits; values below reference white or above 10000 are ignored.
  One Info diagnostic per open names the peak and its source.
- Hardware-decoded HDR frames take the CPU path (the GPU still decodes; frames are copied
  back), so Direct3D11, software and Vulkan output all match.
- The `Decoding ...` line says "HDR10/PQ, tone mapped to SDR".
- Tests: PQ against ST 2084 values, the curve (peak to target, shadows unchanged, monotonic,
  clip-only when the source is no brighter), the mapper (black, peak white, neutral ordered
  grays, reference white, BT.2020 red, strides), the peak policy, option defaults.

Not in slice 1: HLG (needs an OOTF on luminance; shown as before), Dolby Vision profile 5,
dynamic metadata, HDR output.

## Slice 2 - Direct3D11 shader path (implemented)

Acceptance criteria:
- Core: hardware-decoded HDR10 frames stay native and carry `NativeVideoFrameResource.HdrToneMap`
  (`HdrToneMapRequest`: source peak, SDR white). `HdrToneMappingOptions.PreferCpu` (Player
  `--hdr-tonemap-cpu`) keeps slice 1's CPU path for comparison.
- Direct3D11: every hardware frame passes `D3D11VideoProcessorPresenter.BlitTextureToPresentation`
  (native presentation and the composition background). A frame with a request is drawn by
  `D3D11HdrToneMapPresenter`:
  - the decoder's P010 slice is copied into a shader-readable P010 texture with R16 and
    R16G16 plane views;
  - a pixel shader does YCbCr (the frame's matrix and range), PQ, the BT.2390 curve on the
    brightest channel, BT.2020 to BT.709 and gamma 2.4;
  - it draws at the video processor's own source and destination rectangles, over black.
- Without the shader path (not P010, or shader or resource creation failed), the video
  processor gets the PQ input color space, so a driver that tone maps can. One warning names
  the reason and `--hdr-tonemap-cpu`.
- One `D3D11.Hdr` diagnostic says which path runs.
- Test (Windows-only, `D3D11HdrToneMapTests`): the shader's formula with the presenter's
  constants matches the CPU mapper within 2 of 255 levels on black, white, gray, colors and a
  color past the BT.709 gamut; rectangles map to clip and texture space; a dim source is only
  clipped.

## Must Preserve

- ENG-019 SDR color accuracy and the explicit color spaces on every path.
- Hardware decode performance: tone mapping must stay on the GPU for hardware frames.
- CD+G, subtitles and backgrounds compose in SDR over tone-mapped video exactly as now.
- Core boundary: no Direct3D, DXGI or shader code in Core.

## Explicit Non-Goals

- Dolby Vision dynamic metadata (profile 5/7/8 reshaping); play its HDR10 base layer only.
- HDR10+ dynamic metadata in the first slices.
- Color management for wide-gamut SDR monitors (ICC profiles).
- HDR image formats.
