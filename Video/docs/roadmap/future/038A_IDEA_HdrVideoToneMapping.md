# Idea Stage 038A - HDR Video Tone Mapping

Status: Good idea. Owner decision (2026-10-18): add to the roadmap after ENG-019.

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
