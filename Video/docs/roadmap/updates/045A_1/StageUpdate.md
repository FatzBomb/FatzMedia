# Stage Update

Stage: 045A.1 - Hardware decoding on the Avalonia Direct3D11 path
Date: 2026-10-26

## Status: IMPLEMENTED - pending manual smoke

Owner direction (2026-10-26): GPU video rendering on every presentation path. See
`active/045A_ACTIVE_GpuDecodingEveryPath.md` for the root cause, the acceptance criteria, and the
plan for slice 2 (Vulkan on Linux).

Root cause: the Avalonia player asked for software decoding for every renderer
(`AvaloniaPlayerEngineFactory`, `VideoDecodingMethod.Software`). That was right while it had
only frame-sink renderers, which cannot present Direct3D11 frames. It was kept on purpose when
027A slice 2 added Direct3D11 (to compare CPU by presentation path alone). Nothing in the
shared-texture or frame-sink path prevented hardware frames:

- The Direct3D11 renderer draws a D3D11VA texture slice into whichever backend target is
  active: the video processor for SDR, the tone-map shader for HDR. For shared textures that
  target is a private BGRA texture, copied on the GPU into the keyed-mutex texture.
- Core's device adoption (the renderer moves to the decoder's FFmpeg device on its first
  frame) re-attaches every surface. The shared textures are re-exported on that device.

Delivered:

- Avalonia test player (host only; the Avalonia adapter is unchanged):
  - `Renderers.ChooseDecoding` pairs decoding with the renderer:
    - Direct3D11 on a GPU: `Auto` (D3D11VA where the GPU decodes the codec, else software);
    - Direct3D11 on a software rasterizer (the probe's `RequiresSoftwareDecoding`): software;
    - software and Vulkan renderers: software.
  - Each `RendererOption` carries its decoding, and the renderer log line says which one,
    for example `Direct3D11 composition (GPU, ...), hardware decoding (D3D11VA) where the GPU
    supports the codec`.
  - `--decoding=auto|software` (Avalonia only). `software` forces software decoding for every
    renderer, for CPU comparisons, and logs `software decoding (--decoding=software)`.
- Windows-only tests (`D3D11HardwareFrameOffscreenTests`, WARP, 3 tests). A two-slice NV12
  (or P010) texture array on a second WARP device stands in for FFmpeg's D3D11VA pool, frame
  in slice 1:
  1. **Shared textures.** The bridge exports a set on its own device. `EnsureNativeFrameDevice`
     adopts the decoder device, as Core does. Re-attaching retires the first set and exports a
     new one on the decoder device. `PresentNativeFrame` draws the frame. A third WARP device
     opens the textures through their NT handles, like Avalonia's compositor, acquires the
     consumer key, copies, and releases. The pixels are the frame's red.
  2. **Frame sink.** The same adoption, then one readback into the sink, with the frame's red.
  3. **HDR10 on shared textures.** A P010 BT.2020 frame with a tone-map request. The shared
     texture holds the CPU tone mapper's SDR values (within 3 codes), and no CPU fallback was
     requested.

  WARP has no video decoder; these tests cover everything after the decoder. Without a video
  processor (or NV12/P010 support) on WARP the tests stop after the setup: the renderer
  cannot take hardware frames there at all.
- Docs: this note, the stage file, the manifest, `CURRENT_ARCHITECTURE.md` (hardware frames on
  offscreen targets), the Direct3D11 renderer README, and the Avalonia player README.

## Decisions

- **`Auto`, not `Hardware`.** Both try D3D11VA first; `Auto` states the intent (hardware where
  supported) and is what the WinForms player and the decoder re-probe use. The difference is
  only in debug messages.
- **Frame sink: hardware decoding too.** With `--surface=framesink`, hardware decoding means
  GPU decode, GPU composition and one readback per frame.
  - Software decoding does all of that readback work as well, and adds CPU decoding, the
    swscale conversion to BGRA, and an upload per frame.
  - The readback (one BGRA frame at surface size) is the same in both, so hardware decoding
    is never worse whenever the GPU decodes the codec.
  - Precedent: the WPF composited mode (frame sink, 028A) already pairs hardware decoding
    with the readback.
  - Measured below: what hardware decoding removes from the CPU.
- **The software and Vulkan renderers keep software decoding.** They cannot present Direct3D11
  frames, and hardware decoding with a copy back to the CPU is not GPU rendering. Vulkan
  hardware frames are slice 2; the software renderer is the no-GPU fallback.
- **Device sharing stays as it is.** The renderer adopts the decoder's device and immediate
  context (007B). It does not hand its device to FFmpeg. The same command stream orders
  decode, blit and the shared-texture copy, so a pool surface is never recycled under a blit.
  The keyed mutex orders the copy against the compositor's device.
- **No Core change.** The renderer and Core already support the path. The one gap found, a
  dropped frame when a renderer cannot present a hardware frame at all, exists on every path
  and is not reachable with the test players' pairing. It is deferred with a design in the
  stage file.

## Measurements (Linux sandbox)

What software decoding costs the CPU, so what hardware decoding removes. Intel Xeon 2.1 GHz,
4 cores. FFmpeg 9.0.2 (the build in `/opt/fatzbomb-native`), `ffmpeg -benchmark -f null`, user
CPU time per frame, two runs each. The 4K clip was made here with libkvazaar
(`testsrc2`, 3840x2160, 30 fps, 35 Mbit/s).

| Clip | Decode only | Decode + conversion | CPU cores at 30 fps |
|---|---|---|---|
| H.264 High 1080p30 (`h264_1080p.mp4`) | 4.6-4.9 ms | 6.1-6.7 ms (BGRA) | 0.2 |
| HEVC Main 10 1080p30 HDR10 (`hevc_hdr10.mp4`) | 12.4-12.8 ms | 31.0-31.5 ms (RGB48, before the CPU tone mapper) | 0.95 + tone mapper |
| HEVC Main 4K30 (kvazaar) | 45.9 ms | 50.7-52.2 ms (BGRA) | 1.55 (3.1 at 60 fps) |

- 4K HEVC decoded at only 1.6-1.7x real time on all 4 cores. 4K60 would not keep up here.
- The frame-sink readback is the same in both decoding modes: 8.3 MB per frame at 1080p,
  33 MB at 4K.
- Not measured, because there is no Windows GPU here: the D3D11VA decode, the video-processor
  blit, and the readback. The owner's smoke (step 3) gives the Windows CPU figures.

## Verification (Linux sandbox)

- `EnableWindowsTargeting=true dotnet build FatzBomb.MediaControl.sln -c Release`: 0
  warnings, 0 errors.
- Tests (all pass): Portable 1439, Linux 101 (with `FATZBOMB_LINUX_FFMPEG_DIR` and
  `FATZBOMB_LINUX_SAMPLE_VIDEO`), Avalonia headless 33, Vulkan on lavapipe 67.
- The Windows test project, including the 3 new WARP tests, builds. They run on Windows CI
  only, and have not been run.
- `dotnet format --verify-no-changes`: clean. `check-core-boundary`,
  `check-adapter-boundary`, `check-wpf-boundary`, and `check-presentation-parity`: pass.
- Avalonia player on Linux (Xvfb, lavapipe, `--renderer=vulkan`, H.264 sample). The log shows
  `renderer Vulkan: Vulkan composition, software decoding (the Vulkan renderer takes no
  hardware frames yet)` and `Decoding H264 High 8-bit 1920x1080 (...), decoder h264 in
  software`.
- Not verified here: real D3D11VA frames, Avalonia's ANGLE import, CPU on Windows, HDR on a
  GPU. See the manual smoke below.

## Manual smoke (Windows, native DLLs in `FatzBomb.MediaControl.Player.Avalonia/native/`)

Base command:
`dotnet run --project FatzBomb.MediaControl.Player.Avalonia -c Release -f net10.0-windows7.0 -- --renderer=direct3d11 <file> [options]`

1. **Hardware decoding on the shared GPU image.** Run with `C:\media\<film>`.
   - The Log shows:
     - `Renderer: P1: Windows platform, renderer Direct3D11: Direct3D11 composition (GPU,
       NVIDIA GeForce RTX 5090, ...), hardware decoding (D3D11VA) where the GPU supports the
       codec`;
     - `VideoDecoder: Decoding ... decoder <h264|hevc|av1> via d3d11va` (AV1 now uses FFmpeg's
       `av1` decoder on the GPU instead of `libdav1d`);
     - a `D3D11: Shared textures exported: ...` line when the surface attaches, and another
       once the decoder's device is adopted (more after resizes).
   - The status line ends `via shared GPU image`.
   - Play 1 minute with seeks, pause and resume: no black frames, no flicker, no
     `D3D11.DeviceLost`.
2. **Next media.** Open a second file while the first plays. The log shows another
   `Shared textures exported` line (a new decoder device is adopted) and `via d3d11va`. At
   most one black frame at the switch.
3. **CPU.** Play the same 1080p film for 30 s, once as in step 1 and once with
   `--decoding=software`. The second logs `decoder ... in software` and
   `software decoding (--decoding=software)`. Note the status line's CPU % and cores for
   each. The first should be clearly lower.
4. **4K HEVC.** Run `C:\fbtest\4k-hevc.mkv`.
   - The log shows `decoder hevc via d3d11va`.
   - Playback is smooth with no dropped-frame bursts.
   - Repeat with `--decoding=software` and note the CPU for both.
5. **HDR.** Run `C:\fbtest\hdr10.mp4`, then `C:\fbtest\hlg.mp4`.
   - The log shows `via d3d11va` and `HDR10 video is tone mapped to SDR on the GPU (Direct3D11
     shader).` (`HLG ...` for the HLG clip).
   - The picture matches the WPF player on the same clip: not washed out, no clipped
     highlights.
   - `--hdr-tonemap-cpu` still works: it logs CPU tone mapping and looks the same.
6. **Frame sink.** Run step 1 with `--surface=framesink`.
   - The status line ends `via frame sink` (reason `PreferGpuSharedSurface is off`), and the
     log still shows `via d3d11va`.
   - Note the CPU: it should fall between step 1 and `--surface=framesink --decoding=software`.
   - Repeat with the HDR10 clip.
7. **Two players.** Run `--players=2` and open a video in each.
   - Both log `via d3d11va` and `via shared GPU image`, and both play smoothly.
   - Closing or replacing one does not disturb the other.
8. **Second screen.** During playback, open player 1's second screen (the toolbar box, or
   `--second-screen=2` at startup).
   - Both screens show the video, and the status line adds `2nd screen WxH via shared GPU
     image`.
   - Pause, seek, and resize the main window while paused: both screens update. Close the
     second screen: the main one keeps playing.
9. **Resize and mode.** Maximize, restore, and drag the edge during playback and while paused:
   the picture follows with no stale frame. Switch the Mode box to Audio and back to Full:
   video returns on the shared path.
10. **Other renderers.** Switch the toolbar to Software, then Vulkan. The log says `software
    decoding` for each, and the picture plays (frame sink).

## Owner smoke results

Pending.
