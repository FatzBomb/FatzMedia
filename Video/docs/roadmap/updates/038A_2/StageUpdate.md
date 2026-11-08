# Stage Update

Stage: 038A.2 - HDR10 tone mapping in a Direct3D11 shader
Date: 2026-10-18

## Status: IMPLEMENTED - owner smoke passed 2026-10-18 ("everything looked good")

Owner request: "continue, we are good" (after slice 1).

Baseline (slice 1): hardware-decoded HDR10 frames were copied back to the CPU, converted to
16-bit RGB and tone mapped there. That gave the right picture on every renderer, but it costs
a GPU-to-CPU copy, swscale and the mapper for every 4K frame.

Delivered:

**Core**
- `HdrToneMapRequest` (source peak, SDR white), on `NativeVideoFrameResource.HdrToneMap`.
- `VideoDecoder` keeps hardware-decoded HDR10 frames native, with the request and their color
  space (an untagged matrix is BT.2020).
- `HdrToneMappingOptions.PreferCpu` keeps slice 1's CPU path. Software decode and the software
  and Vulkan renderers use the CPU path as before (they never get native frames).
- The decoder's Info line says where the mapping runs: "on the CPU" or "by the renderer".

**Direct3D11**
- `D3D11HdrToneMapPresenter`:
  1. Copies the decoder's P010 texture-array slice into a shader-readable P010 texture.
  2. Reads its luma and chroma planes through R16 and R16G16 views.
  3. Draws a quad with the `PSToneMap` pixel shader. The shader converts YCbCr with the
     frame's matrix and range (10-bit codes in P010's top bits), converts PQ to nits,
     applies the BT.2390 curve to the brightest channel, converts BT.2020 to BT.709 and
     encodes gamma 2.4. It is the CPU mapper's math in floating point.
- Where it runs: `D3D11VideoProcessorPresenter.BlitTextureToPresentation`, the one place
  hardware frames reach the video processor, both for native presentation and for the
  composition background (secondary windows, subtitles). It draws at the processor's own
  rectangles over black, so stretch modes are unchanged.
- Fallback: when the frame is not P010, or the shader or its resources fail, the video
  processor gets the `YcbcrStudioG2084LeftP2020` input color space, so drivers that tone map
  HDR in the video processor can.
- One `D3D11.Hdr` line per player names the path. The fallback is a warning with the reason
  and `--hdr-tonemap-cpu`.
- Swap-chain resizes: the video processor's reset releases the shader's render-target view
  (as it does its own output view); the shaders and textures stay.

**Player**
- `--hdr-tonemap-cpu`.

**Tests**
- `D3D11HdrToneMapTests` (Windows-only) mirrors the shader's formula with the presenter's
  constants. It matches the CPU mapper within 2 of 255 levels on black, peak white, gray,
  three colors and a saturated red past the BT.709 gamut.
- It also checks rectangle mapping and that a dim source is only clipped.
- These were run here through a temporary copy (all pass). Windows CI runs the real file.
- The HLSL was compiled here with glslang's HLSL front end (both entry points). Windows
  compiles it with D3DCompile at first use.

## Review (Sonnet) follow-ups

- **Fallback to the CPU mapper.**
  - Before: when the shader path cannot run (not P010, shader or resource failure, missing
    `d3dcompiler_47.dll`), HDR10 went to the video processor and looked washed out unless the
    driver tone maps.
  - Now each request carries an `HdrToneMapFallback` from the decoder. The renderer trips it,
    and the decoder sends the rest of that file tone mapped on the CPU, with a warning from
    each side.
- **Failures are per device.** A shader-path failure is remembered only for the device it
  happened on. A device change (recovery, a new decoder device) gets another try.
- **Device cleanup releases everything.** `CleanupResources` now fully resets the shader path
  (`ResetDeviceResources`), freeing the P010 copy (about 25 MB at 4K) and the old device.
  Swap-chain resizes still release only the render-target view.
- **Every exception** in the shader path falls back. Before, only Direct3D and compile errors
  did.
- **Edge sampling** stays one luma texel inside the crop, so bilinear taps never reach the
  decoder's padding rows (1080 of 1088) and tint the last line.
- Left as is:
  - Downscaling uses one bilinear tap per pixel (4K into a small window can alias slightly
    on fine detail; the video processor filters more).
  - No chroma siting offset.
  - The `D3D11.Hdr` line is reported once per device.

## Decisions made without the owner

- **Only P010 frames take the shader.** HDR10 is 10-bit, and decoders output P010 for it. An
  8-bit HDR stream (rare) uses the video-processor fallback.
- **Bilinear sampling of luma and chroma.** Chroma is sampled at the luma position (no
  siting offset), as the video processor does. The difference is under a pixel of chroma.
- **No new contract for renderers.** Only Direct3D11 receives native frames. Every other
  renderer forces software decoding and so gets the CPU path.

## Verification

- Linux:
  - The full solution builds in Release with no warnings.
  - `dotnet format --verify-no-changes` is clean, and the boundary scripts pass.
  - Portable 912, Linux 51 and Vulkan 51 tests pass.
  - The HLSL compiles with glslang's HLSL front end.
- Not runnable here: D3D11, hardware decode, real HDR files. Covered by the manual smoke below.

## Manual smoke (Windows, Direct3D11, hardware decoding)

1. Play an HDR10 file.
   - The Log shows `HDR10 video is tone mapped to SDR by the renderer: peak ...` and
     `HDR10 video is tone mapped to SDR on the GPU (Direct3D11 shader).`
   - The picture looks like slice 1's: normal contrast and color, highlights rolling off.
2. Run with `--hdr-tonemap-cpu`. The Log says "on the CPU", and the picture looks the same.
   Small rounding differences are fine.
3. Compare smoothness on a 4K HDR file with and without `--hdr-tonemap-cpu`. The GPU path
   should be at least as smooth, with lower CPU use (Task Manager).
4. Secondary window and stretch modes (UniformFit, Fill, UniformToFill) with an HDR file:
   correct framing, no garbage at the edges, and the same tone mapping in both windows.
5. Resize and go fullscreen during HDR playback: no black frames that stay, and no errors.
6. If the Log shows the `D3D11.Hdr` warning instead, send it. It names why the shader path
   was unavailable. The picture should still be tone mapped: the CPU mapper takes over, and
   a `VideoDecoder` warning says so.
