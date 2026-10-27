# Stage Update

Stage: 045A.2 - GPU decoding for the Vulkan renderer (Vulkan Video on the renderer's device)
Date: 2026-10-27

## Status: IMPLEMENTED - pending manual smoke

Owner decision (2026-10-27): full compatibility. Every GPU renderer decodes on the GPU on every
platform it runs on. This slice gives the Vulkan renderer GPU decoding on Linux and Windows (the
code is the same on both). Slice 3 (`updates/045A_3`) adds the Avalonia shared GPU image for
Vulkan frames on Windows. The Windows smoke steps for both slices are in `updates/045A_3`, because
the owner's RTX 5090 is the first machine with a video decode queue to test on.

## Delivered

- **Core contract (additive).**
  - `IMediaRendererVideoDecodeDevice` (Core, `Graphics/Composition`) is optional on an
    `IMediaRendererBackend`. It names the FFmpeg device type the renderer's device decodes with
    (`vulkan`), or why it cannot. It creates FFmpeg device contexts on that device and
    describes devices and frames. It also answers device-loss checks. Handles stay opaque
    (`AVBufferRef*`, `AVFrame*`, native handles), so Core has no Vulkan types.
  - `MediaGraphicsBackendId.Vulkan` (`"vulkan"`).
  - `RendererVideoDecodeServices` (internal) wraps the platform's
    `IVideoHardwareAccelerationServices` in every player's service provider. All decoders of
    the player use it: the main video, recovery swaps, hardware re-probes and background video.
  - While a renderer device is attached, the wrapper offers only the renderer's device type,
    or none when its device cannot decode. A Vulkan renderer therefore never receives D3D11VA
    frames it cannot draw. With no renderer device attached, the platform decides as before.
  - `MediaPresentationCoordinator` attaches its backend's device to the engine when it is
    built and detaches it on dispose. A renderer without the interface (Direct3D11, software)
    attaches nothing, which returns decoding to the platform.
  - `VideoDecoder` asks the renderer for the device context instead of calling
    `av_hwdevice_ctx_create` for that type. The rest of the hardware path is unchanged:
    `get_format`, cloned frames, queue caps, the startup-failure fallback, device-loss
    recovery and the CPU copy-back for the CPU tone mapper.
- **Vulkan renderer: the device.**
  - The instance is created at Vulkan 1.3 when the loader has it (1.1 otherwise).
  - When the physical device has what FFmpeg 9's Vulkan Video needs, the device is created
    with one extra queue on the video decode family. "What it needs" is: a 1.3 device;
    `VK_KHR_video_queue`, `VK_KHR_video_decode_queue` and the H.264/H.265/AV1/VP9 decode
    extensions it has; a decode queue family for those codecs; timeline semaphores,
    synchronization2 and sampler YCbCr conversion; R8/R8G8 and R16/R16G16 plane views with linear
    filtering.
  - It also enables `VK_KHR_video_maintenance1` and `VK_KHR_push_descriptor` when present, and
    the features FFmpeg enables on devices it creates itself (`device_features_copy_needed`)
    as far as they are supported.
  - If `vkCreateDevice` fails with all that, the device is created as before and the reason
    is kept.
  - `VulkanSharedDevice` owns the instance, device and loader with a reference count. The
    compositor holds one reference, and an FFmpeg device context made from the device holds
    another (dropped by the context's `free` callback). A decoder can therefore never use a
    destroyed device.
  - Every queue submission and wait takes the device's queue lock: the renderer's, and
    FFmpeg's through `lock_queue`/`unlock_queue` callbacks.
- **FFmpeg on the renderer's device.**
  - The renderer fills `AVVulkanDeviceContext` (`av_hwdevice_ctx_alloc`, then
    `av_hwdevice_ctx_init`): instance, physical and logical device, `vkGetInstanceProcAddr`,
    the enabled feature chain and extensions, and the lock callbacks.
  - The queue families are the graphics family (graphics, compute, transfer; FFmpeg's uploads
    and downloads run there) and the decode family with its codecs.
  - One context is created per device and handed out by reference.
  - The FFmpeg structures (`AVVulkanDeviceContext`, `AVVulkanFramesContext`, `AVVkFrame`) are
    declared in `FfmpegVulkanInterop.cs` for FFmpeg 9.0 (libavutil 61), 64-bit. FFmpeg.AutoGen
    does not bind them. A test pins their layout.
- **Frames.**
  - FFmpeg creates the frames context itself. For decoders it already makes sampleable
    multi-planar images with `MUTABLE_FORMAT | EXTENDED_USAGE`, and shares them `CONCURRENT`
    across the families it was given. No frames-context hook and no queue-family ownership
    transfer is needed.
  - The renderer reads the frame through per-plane views (sampling usage only, as FFmpeg's own
    views): R8/R8G8 for 8-bit, R16/R16G16 for 10- to 16-bit. This works for one multi-planar
    image (aspect planes 0 and 1) and for one image per plane, FFmpeg's other layout.
  - `video.frag` converts the frame into the **video layer**, a BGRA image at the display
    size. The layer is then composed like an uploaded software frame (nearest blit, wash, CDG)
    and kept as the retained background for CD+G refreshes and paused redraws.
  - A frame is converted once per composition revision, even when there are several targets.
  - Core's `ClearCompositionBackgroundFrame` forgets the layer.
- **Conversion.**
  - `video.frag` handles BT.601, BT.709 and BT.2020, limited and full range, at any bit depth.
  - For HDR10 and HLG it is a GLSL port of the Direct3D11 tone-map shader: PQ, or HLG through
    the inverse OETF and the BT.2100 OOTF; BT.2390 on the brightest channel; 203-nit SDR
    white; BT.2020 to BT.709; gamma 2.4. The constants are built by
    `VulkanVideoConversion.Create`, with the Direct3D11 shader's terms.
  - The constants go in a uniform buffer, not push constants. Lavapipe misreads
    fragment-stage push constants past offset 0 (see `VulkanOverlayPipelines`).
  - The last row and column are sampled one luma texel inside the picture, as in Direct3D11,
    so the decoder's padding never bleeds in.
- **Synchronization.**
  - The frame is locked with FFmpeg's `lock_frame` while its layout and semaphore value are
    read.
  - The submission waits on the frame's timeline semaphore at `sem_value` and signals
    `sem_value + 1`.
  - The frame state (`sem_value`, `layout = SHADER_READ_ONLY_OPTIMAL`, `access`) is updated,
    then the frame is unlocked right after the submit, before the fence wait. This is the
    pattern FFmpeg's own submissions use.
  - The renderer still waits on its fence every frame, so Core's producer-owned lifetime holds.
- **Native frames and device loss.**
  - `EnsureNativeFrameDevice` accepts frames decoded on the renderer's current device. A frame
    from another device is skipped with one warning; the backend adopts nothing.
  - `PresentNativeFrame` draws the frame as a composition: video over black, at the
    presentation stretch.
  - A `VK_ERROR_DEVICE_LOST` in the renderer marks the shared device lost. The decoder's
    device-loss check (`IsVideoDecodeDeviceLost`) reads that mark and a fence-status probe,
    so `Decoder.DeviceLost` recovery (software, then a hardware re-probe on the new device)
    runs as for Direct3D11.
- **Probe and hosts.**
  - `VulkanPresentationCoordinatorFactory.ProbeAvailability` asks for software decoding only
    when the device cannot decode. Its detail and the `Vulkan device:` log line end with
    `Vulkan Video decoding: H.264, HEVC, AV1` or `no Vulkan Video decoding: <reason>`.
  - The Avalonia player decodes with `Auto` for Vulkan when the probe allows it. Its renderer
    line says `hardware decoding (Vulkan Video) where the GPU supports the codec`, or
    `software decoding (the device has no Vulkan Video decoding)`.
  - The WPF and WinForms players (`--renderer=vulkan`) decode with hardware when the probe
    allows it.
  - `--decoding=software` still forces software decoding everywhere.

## Fallbacks (each keeps software decoding, with one line naming the reason)

| Missing piece | What happens | The line |
|---|---|---|
| No video queue, extension, feature, or a 1.3 device/loader (lavapipe, old drivers) | The probe asks for software decoding; a host that asks for hardware anyway gets software frames | `Vulkan device: ..., no Vulkan Video decoding: <reason>`; renderer line `software decoding (the device has no Vulkan Video decoding)`; Core: `Hardware decoding on the renderer's device is unavailable (<reason>); decoding in software.` |
| FFmpeg built without Vulkan, or FFmpeg refuses the device | The decoder opens in software | `VideoDecoder: Hardware decoding on the renderer's device is unavailable (this FFmpeg build has no Vulkan hardware context ... / FFmpeg refused the renderer's Vulkan device (...)); decoding in software.` |
| The GPU does not decode the stream's codec or profile | FFmpeg's `get_format` drops `AV_PIX_FMT_VULKAN` | `Decoding ..., decoder <name> in software (vulkan configured but not used)` |
| A hardware start yields no frame (AV1 on a GPU without AV1 decode) | The existing startup fallback | `VideoPipeline: Hardware video decode produced no usable startup frame; falling back to software ...` |
| A decoded frame the renderer cannot read (not two-plane YCbCr) | Frame skipped; HDR asks for CPU tone mapping | `Vulkan: A decoded video frame cannot be drawn: <reason>.` |
| Device lost | Decoder recovery, as for Direct3D11 | `Vulkan.DeviceLost`, `Decoder.DeviceLost`, later `Hardware video decode restored` |

## Decisions and deviations from the plan

- **The renderer supplies the device through Core, not through a Linux
  `IVideoHardwareAccelerationServices`.** The plan had the Linux platform services hand out the
  renderer's device. The platform services are created before any renderer exists, and Windows
  needs the same path (slice 3). So the handshake is Core-generic instead: an optional backend
  interface, and a wrapper that every decoder of the player uses. Linux keeps its software-only
  platform services, and Windows keeps D3D11VA for the Direct3D11 renderer. No platform code
  changed.
- **The FFmpeg glue lives in the Vulkan renderer.** Only the renderer knows both its `VkDevice`
  and FFmpeg's Vulkan structures. It uses FFmpeg.AutoGen through its Core reference, with no new
  package reference. Core still sees only opaque pointers.
- **No frames-context hook.** The plan expected the renderer to add `SAMPLED` and
  `MUTABLE_FORMAT | EXTENDED_USAGE` in `get_format`. FFmpeg 9 already does this for decoder
  frames (`vulkan_frames_init`).
- **No queue-family ownership transfers.** FFmpeg makes the images `CONCURRENT` when it is given
  more than one family, and the renderer's graphics family is always one of them.
- **Lock callbacks, not internally synchronized queues.** The renderer does not enable
  `VK_KHR_internally_synchronized_queues`. FFmpeg calls the callbacks, and they take the
  renderer's queue lock.
- **The video layer is composed with the existing nearest blit.** This matches the Vulkan
  renderer's software-frame path and the software renderer. Filtered scaling of the layer is a
  later improvement (ENG-038).

## Verification (Linux sandbox, lavapipe; FFmpeg 9.0.2 LGPL from `scripts/setup-linux-native.sh`)

- `EnableWindowsTargeting=true dotnet build FatzBomb.MediaControl.sln -c Release`: 0 warnings, 0
  errors.
- Tests: Portable 1535, Linux 103, Vulkan 79, Avalonia headless 44, all passing.
  `FATZBOMB_LINUX_FFMPEG_DIR` and `FATZBOMB_LINUX_SAMPLE_VIDEO` were set for the Linux and Vulkan
  runs.
- `dotnet format --verify-no-changes`: clean. `check-core-boundary`, `check-adapter-boundary`,
  `check-wpf-boundary`, `check-presentation-parity`: pass. `check-vulkan-shaders.sh`: ok.
- New tests:
  - `VulkanVideoDecodeTests` (Rendering.Vulkan.Tests):
    - `FfmpegStructs_MatchHwcontextVulkanH`: layout of the three FFmpeg structures.
    - `Conversion_*`: range, depth and tone-map constants.
    - `DeviceWithoutVideoQueue_SaysWhyAndOffersNoDecoding`: the probe detail, the backend's
      null device type and its refused context, on lavapipe.
    - The FFmpeg tests below use a lavapipe device created with Vulkan Video's features but no
      decode queue. FFmpeg **accepted the renderer's device**, allocated frames on it and
      uploaded test pictures (`av_hwframe_transfer_data`, which runs on the renderer's graphics
      queue through the lock callbacks):
      - `Nv12Frame_ConvertsLikeSwscale_AndAdvancesFfmpegsSemaphore`, for a multi-planar image
        and for one image per plane: within 3 codes of swscale with Core's flags (BT.709
        limited), shown at 60x46 of a 64x48 frame. The frame's semaphore value goes up by one
        and its layout is recorded.
      - `Frame_ConvertsOncePerRevision_AndTheLayerIsRetained`: one conversion for two targets,
        the retained layer redrawn, cleared by `ClearVideoLayer`.
      - `P010SdrFrame_ConvertsLikeSwscale`: within 3 codes.
      - `HdrP010Frame_IsToneMappedLikeTheCpuMath` (PQ and HLG, 1000 nits): every pixel within
        3 codes of a double-precision port of the tone-map math.
    - Lavapipe allocated both FFmpeg layouts: `G8_B8R8_2PLANE_420_UNORM` and
      `G10X6_B10X6R10X6_2PLANE_420_UNORM_3PACK16` as multi-planar images, and R8 + R8G8 images
      with `AV_VK_FRAME_FLAG_DISABLE_MULTIPLANE`.
  - `RendererVideoDecodeServicesTests` (portable): the routing; one diagnostic per reason; the
    coordinator attaching and detaching its renderer's device on a real `PipelineMediaEngine`.
  - `RendererDecodeDeviceNativeTests` (Linux, real FFmpeg, H.264 1080p sample): the decoder
    takes its Vulkan device context from the renderer, never from `av_hwdevice_ctx_create`.
    On lavapipe FFmpeg's hwaccel declines the stream and the decoder delivers CPU frames, with
    `in software (vulkan configured but not used)`. A renderer without decoding gets plain
    software decoding.
- The Avalonia player under Xvfb on lavapipe with `--renderer=vulkan` logs:
  - `renderer Vulkan: Vulkan composition (llvmpipe ... (Cpu), no Vulkan Video decoding: the
    Vulkan driver has no Vulkan Video decoding (VK_KHR_video_decode_queue)), software decoding
    (the device has no Vulkan Video decoding)`;
  - `Vulkan device: llvmpipe ... no Vulkan Video decoding: ...`;
  - `decoder h264 in software`.
  It plays through the frame sink.

Not verified here (no video decode queue):

- the decode itself;
- FFmpeg's decode submissions racing the renderer's under the shared queue lock;
- DPB references to frames the renderer has transitioned;
- device-loss recovery;
- CPU use against software decoding;
- RADV, ANV and NVIDIA on Linux.

The owner's Windows smoke (`updates/045A_3`) is the first real test.

## Manual smoke (Linux, optional: a GPU whose driver has Vulkan Video)

Build the Avalonia player (`-f net10.0`) with FFmpeg 9 shared libraries that include Vulkan.
Run `dotnet run --project FatzBomb.MediaControl.Player.Avalonia -c Release -f net10.0 --
--renderer=vulkan --native=<ffmpeg lib dir> <clip>`.

1. The log shows `Vulkan device: <GPU> (...), Vulkan Video decoding: H.264, HEVC, AV1` and the
   renderer line `hardware decoding (Vulkan Video) where the GPU supports the codec`.
2. `VideoDecoder: Decoding ... decoder h264 via vulkan`. Play, seek and pause for a minute: no
   black or corrupt frames, no `Vulkan.DeviceLost`.
3. An HDR10 and an HLG clip: `via vulkan` and the same picture as `--decoding=software`. With
   software decoding the CPU tone mapper draws the picture; with Vulkan Video the shader does.
4. With `--decoding=software`, compare the CPU use.
