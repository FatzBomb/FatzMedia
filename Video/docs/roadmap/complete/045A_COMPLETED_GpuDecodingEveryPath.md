# Completed Stage 045A - GPU Decoding on Every Presentation Path

Prepared: 2026-10-26
Completed: 2026-10-27.
Status: owner smoke passed on Windows (RTX 5090): slice 1 (Direct3D11 on the Avalonia surface,
2026-10-26, `updates/045A_1`), slice 2 (Vulkan Video on the Vulkan renderer's device) and slice 3
(Vulkan on Windows, the Avalonia shared GPU image for Vulkan frames), 2026-10-27
(`updates/045A_2`, `updates/045A_3`). Retest fixes: the Avalonia surface keeps a set's handles
open until the compositor has imported them (fast resizes), and the WinForms frame-sink surface
paints each published frame at once (seeking with two players). Not run by the owner: Linux
drivers (RADV, ANV, NVIDIA) and the optional GPU-reset step; both are in ENG-038.

Owner direction (2026-10-26): "The point of the whole control is GPU video rendering on all
stages, hence the shared control base." WPF and WinForms (native HWND, Direct3D11) decode with
D3D11VA and compose on the GPU. The Avalonia player decoded in software even with
`--renderer=direct3d11` on its GPU-shared surface. Vulkan (Linux, and Avalonia's Vulkan path)
decodes in software too.

Owner decision (2026-10-27): full compatibility. Every GPU renderer decodes on the GPU on every
platform it runs on, Vulkan on Windows included (slice 3), and the Avalonia surface takes Vulkan
frames on Windows as a shared GPU image instead of a frame-sink copy (the 027A slice 2 deferral,
folded in here). Direct3D11 stays the Windows default.

## Why the Avalonia paths decoded in software

It was the host's choice. The shared-texture path has no limitation behind it.

- `AvaloniaPlayerEngineFactory` hard-coded `VideoDecodingMethod.Software`. Stage 027A slice 1
  offered only frame-sink renderers (software, Vulkan). Neither can present a Direct3D11
  hardware frame: `EnsureNativeFrameDevice` returns false, and the frame would be dropped.
  When slice 2 added the Direct3D11 renderer, software decoding stayed on purpose "so a CPU
  comparison isolates the presentation path" (`updates/027A_2`).
- The Direct3D11 renderer presents hardware frames the same way on every backend. The video
  processor (or, for HDR, the tone-map shader) draws the decoder's texture slice into the
  active backend's target texture, and the backend's `Present` publishes it.
  - The shared-texture backend's target is a private BGRA render target. `Present` copies it
    on the GPU into a keyed-mutex texture. Nothing is read back.
  - The managed frame sink reads the same target back once.
- Device sharing already works. On the first frame of a decoder, Core has the renderer adopt
  the FFmpeg D3D11 device and its immediate context (007B: one command stream, so the
  decoder cannot recycle a pool surface while a blit still reads it). Core then re-attaches
  every surface (`MediaPresentationCoordinator.EnsureNativeFrameDevice`). For the Avalonia
  surface this re-exports the shared textures on the decoder's device, and the adapter LUID
  check runs again. The compositor (ANGLE on its own D3D11 device) imports the new set as
  after a resize.
- Real limitations:
  - The software and Vulkan renderers have no hardware-frame import.
  - The Linux platform supplies software-only hardware-decode services
    (`SoftwareOnlyVideoHardwareAccelerationServices`).
  - Slice 2 addresses Vulkan. The software renderer stays the CPU fallback and decodes in
    software.

## Acceptance (slice 1)

1. The Avalonia player's Windows build decodes with D3D11VA (`VideoDecodingMethod.Auto`) when
   the Direct3D11 renderer runs on a GPU. The log shows `decoder ... via d3d11va`. This stays a
   host choice (`Renderers.ChooseDecoding`); the adapter is unchanged.
2. On the shared GPU image path, decoded surfaces stay on the GPU through composition into the
   exported texture: no CPU readback, no upload.
3. The frame-sink path (`--surface=framesink`) keeps working with hardware frames: GPU decode,
   GPU composition, one readback. The decision and its numbers are in `updates/045A_1`.
4. Hardware decoding is not used where it cannot be presented, and the log says why in one
   line:
   - software and Vulkan renderers;
   - Direct3D11 on a software rasterizer (the probe's `RequiresSoftwareDecoding`);
   - `--decoding=software`.
   Where it is used but unavailable, the engine decodes in software with its existing single
   line: no D3D11VA device or codec, or a hardware start that produced no frame. The picture
   is never black because of the decoding choice.
5. HDR10 and HLG hardware frames are tone mapped on the GPU (Stages 038A, 042A) on both
   offscreen paths. The Avalonia surface stays SDR.
6. `--players=2` and the second screen work with hardware frames (each player adopts its own
   decoder device; with a second screen the frame is composed to every target).
7. Tests: Windows-only WARP tests cover the hardware-frame path:
   - a texture array on a second device stands in for the decoder pool;
   - the renderer adopts that device and re-exports the shared textures on it;
   - the frame is drawn into the keyed-mutex texture and read by a third device;
   - the same for the frame sink, and for a P010 HDR10 frame through the tone-map shader.
   Release build warning-clean, Portable/Linux/Avalonia/Vulkan tests pass, `dotnet format`
   clean, boundary scripts pass.
8. Manual smoke on Windows passes (`updates/045A_1`).

## Slice 2 plan: GPU decoding for the Vulkan renderer (Linux)

Implemented 2026-10-27, pending manual smoke (`updates/045A_2`, with the deviations from this
plan). The sandbox has only lavapipe and no VAAPI driver, so the decode itself cannot run here;
FFmpeg accepting the renderer's device, the conversion and the frame synchronization are tested
with frames FFmpeg allocates and uploads on it. A copy-back stopgap was considered and rejected
(see below).

### What the sandbox shows (FFmpeg 9.0.2 LGPL build in `/opt/fatzbomb-native`)

- The build is `--enable-vulkan --enable-vaapi --enable-libdrm --enable-ffnvcodec
  --enable-cuda-llvm` (LGPL v3, no `--enable-gpl`). `ffmpeg -hwaccels` lists cuda, vaapi, qsv,
  drm, opencl, vulkan and amf.
- Decoders with a Vulkan hardware config: `h264`, `hevc`, `av1`, `vp9`. VAAPI also covers
  `vp8`, `mpeg2video` and `vc1`. `libdav1d` has none, so AV1 hardware decoding goes through
  FFmpeg's native `av1` decoder, as Core already does on Windows.
- `av_hwdevice_ctx_create(VULKAN)` succeeds on lavapipe (Vulkan 1.4; it enables
  `VK_KHR_external_memory_fd` and `VK_EXT_external_memory_dma_buf`). Lavapipe has no video
  queue, so `-hwaccel vulkan` decodes in software without an error.
- Lavapipe has `samplerYcbcrConversion`, `timelineSemaphore` and `synchronization2`, but no
  `VK_EXT_image_drm_format_modifier`. VAAPI device creation fails (no driver).

### Chosen approach: FFmpeg Vulkan Video on the renderer's own VkDevice

- **Device sharing.** The renderer owns the device (it already creates its own instance,
  device and queue, and exports images for the Avalonia compositor). When the physical device
  has a queue family with `VK_QUEUE_VIDEO_DECODE_BIT_KHR`, the renderer creates the device
  with:
  - Vulkan 1.3;
  - `VK_KHR_video_queue`, `VK_KHR_video_decode_queue`, `VK_KHR_video_decode_h264`/`h265`/`av1`,
    `VK_KHR_video_maintenance1` (when present), `VK_KHR_push_descriptor`, and the features
    FFmpeg's Vulkan device context checks (at least `timelineSemaphore`, `synchronization2`
    and `samplerYcbcrConversion`; take the exact list from the FFmpeg version shipped);
  - the decode queue family besides its graphics family.
  It hands the device to FFmpeg:
  - `av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_VULKAN)`;
  - fill `AVVulkanDeviceContext`: `inst`, `phys_dev`, `act_dev`, `device_features`, the
    enabled instance and device extension lists, `get_proc_addr`, the `qf[]` queue families
    with their video capabilities, and `lock_queue`/`unlock_queue` callbacks that take the
    renderer's queue lock;
  - `av_hwdevice_ctx_init`.
  This is the reverse of Direct3D11, where the renderer adopts the decoder's device. Here the
  renderer must keep its export extensions and pipelines, and FFmpeg accepts a supplied
  device. Core's contract does not change: `IVideoHardwareAccelerationServices` gains a Linux
  implementation, and the renderer supplies the device through a new platform/renderer
  handshake (an additive, optional interface on the renderer backend, so Core stays free of
  Vulkan types).
- **Frames.** `AV_PIX_FMT_VULKAN`, `AVVkFrame`. FFmpeg 9 decodes into one multi-planar image:
  - 8-bit: `VK_FORMAT_G8_B8R8_2PLANE_420_UNORM` (NV12);
  - 10-bit: `VK_FORMAT_G10X6_B10X6R10X6_2PLANE_420_UNORM_3PACK16` (P010).
  The frames context (created in the `get_format` callback from
  `avcodec_get_hw_frames_parameters`) adds `VK_IMAGE_USAGE_SAMPLED_BIT` and
  `VK_IMAGE_CREATE_MUTABLE_FORMAT_BIT | VK_IMAGE_CREATE_EXTENDED_USAGE_BIT`. The renderer can
  then view plane 0 as `R8`/`R16` and plane 1 as `R8G8`/`R16G16` and convert in its own
  fragment shader, with no `VkSamplerYcbcrConversion` (whose immutable samplers would need a
  pipeline per format).
- **Conversion.** A new SPIR-V fragment shader for YCbCr (BT.601/709/2020, limited and full
  range) to BGRA. A GLSL port of the Direct3D11 HDR tone-map shader (PQ and HLG, BT.2390,
  203-nit SDR white) for P010. The existing composition (wash, CDG, subtitles, transforms) then
  works on the converted layer as on an uploaded BGRA layer.
- **Synchronization.**
  - Before sampling, the renderer's submit waits on `AVVkFrame.sem[0]` at `sem_value[0]`
    (timeline semaphore). After its last read it signals the same semaphore at `+1` and
    updates `sem_value`, `layout`, `access` and `queue_family`, under
    `AVVulkanFramesContext.lock_frame`/`unlock_frame`.
  - Queue-family ownership: the frames must be usable from the decode and the graphics
    families, either created concurrent across both or handed over with release/acquire
    barriers (`AVVkFrame.queue_family` says where a frame is).
  - The renderer already waits on its fence every frame, so Core's producer-owned lifetime
    holds: a cloned `AVFrame` is released only after the next frame is presented.
- **Fallback.** Each of these keeps software decoding, with one diagnostic naming the missing
  piece:
  - no video queue (lavapipe, older drivers) or a missing extension or feature: the renderer
    creates its usual device and the Linux services return no device types;
  - the decoder does not support the stream's profile: FFmpeg's `get_format` offers no
    `AV_PIX_FMT_VULKAN`;
  - a hardware start that yields no frame: the existing `TryFallbackToSoftwareVideo`.
  Device loss is shared by decoder and renderer: the renderer's `VK_ERROR_DEVICE_LOST`
  recovery must also trigger `Decoder.DeviceLost`. `IsDeviceLost` gets a Vulkan probe.
- **Avalonia.** Unchanged: the renderer still exports its composed RGBA8 image (opaque fd plus
  semaphores) to the compositor. Decoding adds no new surface kind.
- **Tests that can run here (lavapipe).**
  - Device-capability probing and the selection logic (video queue present or not, the
    extension list, the fallback reason).
  - The YCbCr and HDR shaders: an NV12/P010 multi-planar image on lavapipe goes through the
    conversion and its pixels are compared with the software path. This needs lavapipe's
    format support for the 2-plane formats, to be checked first.
  - `TryOpen` falls back to software on lavapipe with the real FFmpeg build.
- **Validation needed outside the sandbox.** Mesa RADV (AMD) and ANV (Intel) with video
  decode, and NVIDIA 550+ proprietary, on X11 and Wayland, through the Avalonia OpenGL and
  Vulkan backends.

### Alternatives considered

- **VAAPI with DRM PRIME import.** Decode with VAAPI, export each surface as dma-buf fds
  (`vaExportSurfaceHandle`, `VA_SURFACE_ATTRIB_MEM_TYPE_DRM_PRIME_2`, or
  `av_hwframe_map` to `AV_PIX_FMT_DRM_PRIME`), and import them as `VkImage`s. This needs
  `VK_EXT_external_memory_dma_buf` + `VK_EXT_image_drm_format_modifier` (explicit per-plane
  offsets, pitches and modifiers) + `VK_KHR_external_memory_fd`.
  - Synchronization is implicit: `vaSyncSurface`, or `DMA_BUF_IOCTL_EXPORT_SYNC_FILE`
    imported as a Vulkan semaphore.
  - It reaches drivers without Vulkan Video, such as older Intel and AMD Mesa stacks.
  - Costs: modifier negotiation, an import cache keyed by `VASurfaceID`, implicit-sync
    corner cases, and weak NVIDIA support (`nvidia-vaapi-driver`).
  - Kept as a later option if Vulkan Video coverage is too thin.
- **Hardware decode with copy-back (`av_hwframe_transfer_data`).** It needs no renderer change
  (Core's transfer path already turns hardware frames into BGRA). Rejected as a default:
  - it is not GPU rendering;
  - reading back VAAPI surfaces is slow on some drivers (uncached VRAM mappings on AMD);
  - the conversion stays on the CPU;
  - it cannot be measured here.
  It could become an opt-in for the software renderer later.

## Slice 3 plan: Vulkan on Windows (owner 2026-10-27)

Implemented 2026-10-27, pending manual smoke (`updates/045A_3`). Deviation: Direct3D11 cannot
open Vulkan's opaque memory handles, so for the ANGLE compositor Direct3D11 creates the
keyed-mutex texture and Vulkan imports it (the plan had it the other way round). It builds on
slice 2's decoder (FFmpeg Vulkan Video on the renderer's own
`VkDevice`); the decode code is shared, only the platform edges differ. The owner's machine is
Windows (RTX 5090), so slice 3 is the first place the slice 2 decoder can be smoke tested on real
hardware; consider building slices 2 and 3 together and validating on Windows first.

1. **Decode.** `--renderer=vulkan` on Windows decodes with Vulkan Video (H.264, HEVC, AV1, VP9
   where the driver reports them), with the slice 2 fallbacks to software decoding. The Windows
   FFmpeg build must include Vulkan (`docs/NATIVE_DEPENDENCIES.md` names the expected build; check
   `av_hwdevice_ctx_create(VULKAN)` and log the reason when it is missing).
2. **Avalonia shared image on Windows** (deferred in `updates/027A_2`). Export the Vulkan images
   as `VulkanOpaqueNtHandle` (`VK_KHR_external_memory_win32`), which the Core contract already
   names, and import them in Avalonia:
   - into the default ANGLE/Direct3D11 compositor: open the NT handle as a D3D11 texture, with
     `VK_KHR_win32_keyed_mutex` so the existing keyed-mutex consumer path applies unchanged;
   - into Avalonia's Vulkan backend: semaphores (`VK_KHR_external_semaphore_win32`,
     `MediaSharedGpuSemaphoreHandleKind.VulkanOpaqueNtHandle`);
   - queue-family ownership transfer to `VK_QUEUE_FAMILY_EXTERNAL` on hand-over;
   - same-GPU check by LUID (`VkPhysicalDeviceIDProperties.deviceLUID`); a mismatch keeps the
     frame sink with a logged reason, as today.
3. **WPF and WinForms with `--renderer=vulkan`** keep the frame sink in this slice (GPU decode,
   one readback). A Vulkan swap chain on the native window (`VK_KHR_win32_surface`) is a
   possible later step; Direct3D11 already covers the native window.

Acceptance: on the owner's RTX 5090, `--renderer=vulkan` in the Avalonia player logs Vulkan
Video decoding and `via shared GPU image`; WPF and WinForms log Vulkan Video decoding; CPU use is
measured against `--decoding=software`; every fallback logs its reason.

## Not in this stage (deferred, ENG-037)

- **A Core safety net when the renderer cannot present a hardware frame** (the renderer's
  `EnsureNativeFrameDevice` returns false: a host that pairs hardware decoding with the
  software renderer, or a failed device adoption). The frame is dropped today on every path
  (WPF, WinForms, Avalonia). Hosts avoid it by pairing decoding with the renderer, as all
  three test players do. Proposed fix: a per-decoder fallback token on
  `NativeVideoFrameResource` (like `HdrToneMapFallback`), set by the coordinator. The
  pipeline then swaps to a software decoder at the current position (the device-loss swap
  path), so a paused first frame is redrawn too.
- **Decoding on the compositor's GPU.** FFmpeg's D3D11VA device uses the default adapter, as
  do the renderer's own device and Avalonia's ANGLE, so hardware decoding adds no mismatch
  that software decoding did not have. If the compositor ever runs on another GPU (hybrid
  laptops), the LUID check declines the shared textures and the surface uses its frame sink
  (GPU decoding, one readback). Creating both the renderer and the decoder on the
  compositor's adapter (an adapter index for `av_hwdevice_ctx_create`, chosen by LUID) is
  deferred.
- **A device removed during re-export.** Hardware decoding re-exports the shared textures
  whenever a new decoder device is adopted (each media open).
  - The renderer no longer adopts a decoder device that is already removed
    (`D3D11DeviceResources.IsDeviceRemoved`).
  - A removal between adoption and re-export can still make the re-export fail. The Avalonia
    surface then stays on the frame sink for the session (sticky, `updates/027A_2`). The
    picture keeps working, and the shared path returns when the surface gets another player
    (a renderer change in the test player).
  - Telling a transient device-removed failure from a permanent one in `OnUnavailable` is
    deferred.
- **Hardware decode for the software renderer** (copy-back). The software renderer is the
  no-GPU fallback. (Vulkan on Windows moved into slice 3, owner 2026-10-27.)
