# Idea Stage 042A - HDR Follow-Ups

Status: Idea. Deferred from 038A (completed 2026-10-18), which tone maps HDR10 (PQ) to SDR on
the CPU for every renderer and in a Direct3D11 shader for hardware-decoded video.

## HLG tone mapping

HLG (broadcast HDR) is still shown as SDR without tone mapping; the `Decoding ...` line says
so. HLG needs its OOTF: display light depends on each pixel's scene luminance (gamma 1.2 at
1000 nits), so the 038A tables indexed by the brightest channel do not apply directly.
- CPU: inverse HLG OETF per channel, luminance, the OOTF, then the existing BT.2390 curve and
  gamut and gamma steps.
- Direct3D11: the same in `PSToneMap`, behind a transfer constant.
- Tests mirror 038A's: reference values, monotonic grays, shader formula against the CPU.

## HDR output on HDR displays

When Windows HDR is on for the monitor, present HDR video as HDR instead of tone mapping:
- an FP16 or 10-bit swap chain with the matching DXGI color space (`SetColorSpace1`) and
  HDR10 metadata (`SetHDRMetaData`);
- SDR layers (CD+G, subtitles, backgrounds) composed at the system's SDR white level;
- per-monitor detection (`IDXGIOutput6.GetDesc1`), so a window moving between an SDR and an
  HDR monitor switches.

## Must Preserve

- 038A's SDR output, the CPU fallback, and identical results across renderers.
