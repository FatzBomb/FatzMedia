# Stage Update

Stage: 042A.1 - HLG to SDR tone mapping
Date: 2026-10-24

## Status: IMPLEMENTED - pending manual smoke

Owner request: stage 042A, HDR follow-ups; slice 1 is HLG tone mapping (deferred from 038A).

Baseline: HLG video (broadcast HDR, ARIB STD-B67) was shown as SDR without tone mapping, and the
`Decoding ...` line said so. HDR10 (PQ) was tone mapped since 038A.

Delivered:

**Core**
- `HdrTransferFunction` (`Pq`, `Hlg`), and `HdrToneMapRequest.Transfer` (default `Pq`) plus
  `HlgSystemGamma`. For HLG the request's `SourcePeakNits` is the nominal display peak
  (1000 nits).
- `Core/Video/Hdr/HdrToneMapper.cs`:
  - `HlgCurve`: the BT.2100 HLG OETF and its inverse, the system gamma
    `1.2 + 0.42 log10(Lw / 1000)`, and the OOTF `Lw * Ys^(gamma - 1) * E`.
  - `HdrToneMapper` takes a transfer. The PQ path is unchanged.
- HLG per pixel, on 16-bit RGB48 from swscale (BT.2020 matrix, full range):
  1. Inverse OETF per channel to scene light E: a 4096-entry table on the top 12 bits, as for PQ.
  2. Scene luminance Ys = 0.2627 R + 0.6780 G + 0.0593 B.
  3. The brightest channel's display light is m * Lw, with m = Ys^(gamma - 1) * max(E).
  4. The BT.2390 curve maps it (source peak Lw = 1000, target SDR white), and each channel
     keeps its ratio to the brightest: linear = E / max(E) * curve(m * Lw) / SDR white.
     The OOTF's luminance factor cancels out of the ratios, so it only matters for the peak.
  5. BT.2020 to BT.709 with clipping, then gamma 2.4: PQ's tables.
- No `pow` or `exp` per pixel. `Ys^(gamma - 1)` and `curve(m * Lw)` are tables indexed by the
  float's exponent and top 7 mantissa bits (128 steps per octave, 24 octaves, 3073 entries). The
  relative error is under 0.4% at every level. The curve table holds the mapped luminance,
  which is monotonic, so grays can only rise. `1 / max(E)` is a table on the brightest channel's
  index.
- `VideoDecoder`:
  - It tone maps a frame whose transfer (frame tag, else stream tag) is `arib-std-b67`, under
    the same options as HDR10.
  - The request carries the transfer. Hardware frames go to the renderer with it (Direct3D11)
    or are copied back for the CPU mapper (`--hdr-tonemap-cpu`, the renderer's fallback).
  - One Info line per open: `HLG video is tone mapped to SDR on the CPU | by the renderer:
    display peak 1000 nits (BT.2100 nominal, system gamma 1.2), SDR white at 203 nits.`
  - The `Decoding ...` line says `HLG HDR, tone mapped to SDR`. With `--no-hdr-tonemap` it
    still says `HLG HDR, shown as SDR without tone mapping`.
  - The renderer-fallback warning names HLG or HDR10.

**Direct3D11** (two files, local to the HDR path)
- `D3D11HdrToneMapPresenter`:
  - New constant `ToneMapConstants.Transfer`: HLG flag, nominal peak, system gamma - 1.
  - `PSToneMap` converts HLG with `HlgToNits` (inverse OETF per channel, OOTF on BT.2020
    luminance) where PQ uses `PqToNits`.
  - The curve, gamut and gamma steps are shared.
- `D3D11VideoProcessorPresenter`:
  - The video-processor fallback declares `YcbcrStudioGhlgTopLeftP2020` for HLG, so a driver
    that tone maps HLG can.
  - The `D3D11.Hdr` line names HLG or HDR10.

**Software and Vulkan renderers**
- No change needed. They never get native frames (they force software decoding), so HLG
  reaches them already tone mapped on the CPU, as HDR10 does.
- The real-media test below checks the software path; the screenshots show the same picture on
  Vulkan.

**Tests**
- `HlgToneMapperTests` (Portable, 16 cases):
  - inverse OETF and OETF reference values (0, 0.25, 0.5, 0.75, 1);
  - system gamma at 400, 1000 and 2000 nits;
  - 75% HLG shows at 203 nits (BT.2408), and a pure blue shows at about 568 nits;
  - the request's transfer and gamma;
  - black and peak white;
  - every 10-bit gray is neutral and no darker than the one below, and distinct levels are
    distinct;
  - HLG reference white comes out the same as PQ's 203 nits, within 1 level;
  - BT.2020 red stays red;
  - 4000 random 10-bit colors match a double-precision version without tables, within 1 level;
  - an image converts in parallel bands.
- `VideoColorSpaceTests.TheLogDescription_SaysHlgIsToneMapped`: the `Decoding ...` wording.
- `D3D11HdrToneMapTests` (Windows-only; 10 new cases):
  - The shader formula, mirrored with the presenter's constants, matches the CPU HLG mapper
    within 2 levels. The 9 cases are black, peak, 75%, mid gray, a shadow, three colors and a
    red past the BT.709 gamut.
  - The transfer constant selects HLG and its OOTF.
  - Run here through a temporary copy with a stub of the presenter's constants: all 19 pass.
    Windows CI runs the real file.
- `HdrToneMappingNativeTests` (Linux, opt-in with `FATZBOMB_LINUX_FFMPEG_DIR`; 2 cases):
  - For `hevc_hlg.mp4` and `hevc_hdr10.mp4` in `/opt/fatzbomb-native/media` (or
    `FATZBOMB_LINUX_HDR_MEDIA_DIR`), it plays the clip through the software decoder and
    renderer and keeps a presented frame.
  - It checks the tone-map lines, no white clipping (under 2% of pixels), a mean level between
    60 and 200, under 5% black, and that the red bar stays red.
  - With tone mapping off, the frame differs.
- `PSToneMap` compiles with glslang's HLSL front end (both entry points).

## Measured (Linux sandbox, 4 cores, shared with other sessions)

- The mapper alone on a 3840x2160 frame: HLG 44 ms and PQ 28 ms. HLG reads more tables and
  does one more multiply per channel.
- The real clips (HEVC Main 10, 1920x1080, 30 fps, software decode) play at 30 fps in the
  Avalonia test player when the machine is not busy.
- Tone-mapped frame of each clip:

| Clip | Mean luma | White-clipped | First bar |
|---|---|---|---|
| `hevc_hlg.mp4` | 121.9 | 0% | 255,0,0 |
| `hevc_hdr10.mp4` | 115.0 | 0% | 255,0,0 |

  Without tone mapping, both have a mean luma of 127.5.
- Both clips are color bars encoded with HDR tags. The yellow bar's encoded values are
  R'=1.0, G'=0.92 (BT.2020). As tagged, that is orange-yellow in PQ (G is half of R in nits)
  and golden in HLG. The tone mapper shows it the way the tags say.
- Screenshots (Avalonia test player under `xvfb-run`, 1600x1000, frame-sink surface):
  - `/opt/fatzbomb-native/screenshots/042a-hlg-software.png`
  - `/opt/fatzbomb-native/screenshots/042a-hlg-vulkan.png`
  - `/opt/fatzbomb-native/screenshots/042a-hdr10-software.png`
  - `/opt/fatzbomb-native/screenshots/042a-hdr10-vulkan.png`

  Each one shows the bars tone mapped, and the Log shows the `Decoding ...` and tone-mapping
  lines.

## Decisions made without the owner

- **Nominal peak 1000 nits, system gamma from BT.2100's formula.**
  - HLG carries no peak metadata. BT.2100's reference display is 1000 nits, so gamma is 1.2.
  - The formula is in `HlgCurve.SystemGamma`, so slice 2 can show HLG on an HDR display with
    the display's own peak.
  - HLG's reference white (75%) lands at 203 nits, exactly PQ's, so both look alike with the
    same SDR white.
- **Same curve and hue handling as HDR10.** The BT.2390 curve runs on the brightest channel's
  display light, with every channel keeping its ratio. Very bright saturated colors stay
  saturated, as for PQ.
- **HLG is taken as BT.2020** (primaries and luminance weights), like HDR10. HLG tagged with
  BT.709 primaries is rare and comes out slightly oversaturated.
- **The OOTF is on the display side** (BT.2100's OOTF for a reference display), not BT.2390's
  scene-referred conversion to PQ. The two are the same at 1000 nits.

## Verification

- Linux:
  - `EnableWindowsTargeting=true dotnet build FatzBomb.MediaControl.sln -c Release`: no
    warnings.
  - `dotnet format --verify-no-changes`: clean.
  - Boundary scripts (adapter, core, presentation parity, WPF): pass.
- Tests pass:
  - Portable: 1191 (17 new).
  - Linux: 59, with FFmpeg 9 set (2 new native tests).
  - Avalonia: 33.
  - Vulkan: 62.
- Not runnable here: Direct3D11, hardware decode, the WPF Player and a real HDR monitor.
  Those are covered by the manual smoke below.

## Manual smoke (Windows)

Use an HLG clip (for example a BBC or NHK HLG sample, HEVC 10-bit, `arib-std-b67`) and an HDR10
clip. Set Windows HDR off first (an SDR monitor, or HDR off in Settings > Display).

1. HLG, hardware decoding (Direct3D11, default):
   - The Log shows `Decoding ... HLG HDR, tone mapped to SDR`,
     `HLG video is tone mapped to SDR by the renderer: display peak 1000 nits (BT.2100
     nominal, system gamma 1.2) ...` and `D3D11.Hdr: HLG video is tone mapped to SDR on the
     GPU (Direct3D11 shader).`
   - The picture has normal contrast and color: not washed out, highlights roll off.
2. The same HLG clip with `--hdr-tonemap-cpu`:
   - The Log says "on the CPU".
   - The picture looks the same as in step 1 (small rounding differences are fine).
3. The same HLG clip with `--no-hdr-tonemap`: the old look (a little brighter and flatter) and
   `shown as SDR without tone mapping`.
4. HDR10 clip: unchanged from 038A (`HDR10 video is tone mapped ...`, same picture as before).
5. `--renderer=software` (and Vulkan, if usable) with both clips: the same picture as
   Direct3D11.
6. Compare the HLG clip with mpv (default settings) or the Windows Media Player app.
   Similar brightness and color are expected.
7. With Windows HDR on (Settings > Display > Use HDR), play both clips again. This slice still
   tone maps to SDR, and Windows shows the SDR picture at its SDR content brightness.
   - Check that nothing breaks or flickers.
   - Move the window to the other monitor, if you have one. The picture should stay the same.
   - Real HDR output is slice 2.
