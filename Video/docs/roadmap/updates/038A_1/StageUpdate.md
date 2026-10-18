# Stage Update

Stage: 038A.1 - HDR10 to SDR tone mapping on the CPU path
Date: 2026-10-18

## Status: IMPLEMENTED - pending manual smoke

Owner request: "038a", after ENG-019 left HDR files washed out (flagged by the `Decoding ...`
line).

Baseline: HDR10 video (PQ transfer, BT.2020 primaries) was converted as if it were SDR, on
both the GPU video processor path and the CPU (swscale) path, so it looked washed out and dim.

Delivered:

**Core**
- `HdrToneMappingOptions` on `MediaPlayerConfiguration.HdrToneMapping`, on by default:
  - `Disabled` shows HDR as before, for comparison.
  - `ReferenceWhiteNits` (default 203, BT.2408's HDR reference white, also mpv's SDR target)
    is the SDR target. The source's range is compressed into it, so the source peak becomes SDR
    white and 203 nits itself lands at about 230 of 255 for a 1000-nit source.
- `Core/Video/Hdr/HdrToneMapper.cs`:
  - `PqCurve`: SMPTE ST 2084.
  - `Bt2390ToneCurve`: the BT.2390 EETF, a Hermite knee in PQ space with no black lift.
  - `HdrToneMapper`: converts full-range RGB48 (PQ, BT.2020) to BGRA8 (BT.709, gamma 2.4).
- Per pixel, the mapper:
  - converts PQ to linear light;
  - applies the curve's gain, taken from the brightest channel, to all three channels, so
    hue is kept;
  - converts BT.2020 to BT.709 primaries and clips;
  - encodes gamma 2.4.
- Tables, 4096 entries each:
  - the PQ tables use the top 12 bits of the signal (the source is 10-bit);
  - the gamma table is indexed by the square root of linear light, so shadow steps stay far
    below one 8-bit level.
- The mapper runs in parallel 16-row bands.
- `VideoDecoder`:
  - It tone maps a frame when its transfer (frame tag, else stream tag) is PQ.
  - swscale converts the frame to full-range RGB48 with the BT.2020 matrix (ENG-019's color
    space path) into a reused scratch buffer, and the mapper writes the pooled BGRA frame.
  - Hardware-decoded HDR frames skip native frame output and take this path. The GPU still
    decodes; frames are copied back.
  - The curve's peak comes from MaxCLL, else the mastering display's peak (frame side data,
    else stream side data), else 1000 nits. Values below reference white or above 10000 are
    ignored.
  - One Info line per open: `HDR10 video is tone mapped to SDR: peak N nits (source), SDR
    white at 203 nits.`
- The `Decoding ...` line now says "HDR10/PQ, tone mapped to SDR". With `--no-hdr-tonemap` it
  still says "shown as SDR without tone mapping (looks washed out)".

**Player**
- `--no-hdr-tonemap` turns tone mapping off for comparison.

**Tests (`HdrToneMapperTests`, 18 cases)**
- PQ against ST 2084 reference values.
- The curve: peak to target, shadows unchanged, monotonic, clip-only for a dim source.
- The mapper: black, peak white, neutral ordered grays, reference white between 200 and 250,
  BT.2020 red stays pure red, strides.
- The peak policy and option defaults.

## Review (Sonnet) follow-ups

- The pipeline's decoder re-creation (device-lost swap, software fallback) now carries the HDR
  options; it used the defaults, so `--no-hdr-tonemap` was lost after a fallback.
- An untagged matrix on tone-mapped HDR10 resolves to BT.2020, not the size-based BT.709.
- A frame's unusable MaxCLL or mastering peak (0 or a placeholder) falls back to the stream's
  value instead of straight to the assumed 1000 nits.
- Wording: the source peak, not 203 nits, becomes SDR white.

## Measured (Linux sandbox, 4 cores)

- The real decoder with FFmpeg 9.0.2, on a tagged 3840x2160 10-bit PQ clip (FFV1, software
  decode): 90 ms per frame with tone mapping and 62 ms without. The difference covers
  swscale's RGB48 conversion and the mapper.
- The mapper alone on 4K: 40 ms per frame on smooth content (4 cores), and it scales evenly
  with cores (159 ms on one).
- Expect a few milliseconds per 4K frame on a current desktop CPU. Slice 2 (a GPU shader)
  removes the cost on Direct3D11.

## Decisions made without the owner

- **The engine's own tone mapper, not FFmpeg's `zscale`/`tonemap`.** Using the FFmpeg filters
  would tie the picture to each host's FFmpeg build, and the float `zscale` chain is slow at
  4K. The shipped 9.0.2 LGPL build does have `zscale`, `tonemap` and `libplacebo`.
- **Defaults match mpv's SDR output:**
  - BT.2390 curve;
  - SDR white at 203 nits;
  - gamma 2.4 (BT.1886), so converted HDR looks like an SDR grade does in this player.
- **Gain from the brightest channel:** hue is kept; very bright saturated colors stay
  saturated rather than desaturating toward white.
- **HDR always takes the CPU path in slice 1.** Every renderer shows the same picture, and
  Direct3D11 gets the GPU path in slice 2.
- **Primaries are taken as BT.2020** (HDR10's). The rare PQ file tagged with P3 primaries
  comes out slightly oversaturated. An untagged YCbCr matrix on HDR10 is taken as BT.2020 too.
- **HLG is not tone mapped yet.** It needs a luminance-dependent OOTF. It is rare outside
  broadcast and still shows as before, and the log line says so.

## Verification

- Linux:
  - The full solution builds in Release with no warnings.
  - `dotnet format --verify-no-changes` is clean, and the boundary scripts pass.
- Tests pass: Portable 912, Linux 51, Vulkan 51, Avalonia 14.
- Not runnable here: hardware decode, the Direct3D11 Player and real HDR files. Those are
  covered by the manual smoke below.

## Manual smoke (Windows)

1. Play an HDR10 file (the Log's `Decoding ...` line says `HDR10/PQ, tone mapped to SDR`).
   - The Log shows `HDR10 video is tone mapped to SDR: peak ... nits (MaxCLL | mastering
     display | assumed ...)`.
   - The picture has normal contrast and saturation, neither washed out nor clipped.
   - Highlights roll off instead of turning flat white. Skin tones look natural.
2. Compare against another player (mpv with default settings is the closest match; VLC and
   the Windows Media Player app also tone map). Small differences in brightness are expected.
3. Run the Player with `--no-hdr-tonemap`: the same file looks washed out, as before.
4. Play an SDR file: no change from before (no tone mapping line).
5. Smoothness: watch a 4K HDR file for a minute; note any stutter or dropped frames
   (Log statistics). Slice 1 copies GPU-decoded frames back to the CPU, so this is the cost to
   watch.
6. Optional: a secondary window, and `--software`. The HDR picture is the same everywhere.
