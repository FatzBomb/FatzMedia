# FatzBomb.MediaControl.Rendering.Vulkan

Preview Vulkan renderer backend for `FatzBomb.MediaControl` (Stage 026A slices 1-2).

```csharp
if (VulkanPresentationCoordinatorFactory.IsSupported(out var reason))
    factory = new LinuxMediaControlPlatformFactory(new VulkanPresentationCoordinatorFactory());
```

The backend renders into a managed BGRA frame-sink surface: GPU clear, background upload and
nearest blit (same placement rules as the software renderer), then a SPIR-V render pass that
blends the wash and the CDG overlay (palette-expanded on the CPU, nearest-sampled with the
software renderer's UniformFit mapping), and readback. Frames with subtitles or base-visual
transforms are rejected with a diagnostic. Window swapchains are a later slice.

## GPU decoding (Stage 045A slice 2)

When the GPU has a Vulkan Video decode queue (and the device is Vulkan 1.3 with timeline
semaphores, synchronization2 and sampler YCbCr conversion), the renderer creates its device
with that queue. It then lets FFmpeg decode on it: the backend implements Core's
`IMediaRendererVideoDecodeDevice` and hands FFmpeg an `AVVulkanDeviceContext` for its own
device (`VulkanSharedDevice`, `FfmpegVulkanInterop.cs`).

- Decoded NV12/P010 frames are read through per-plane views.
- `Shaders/video.frag` converts them into a BGRA video layer (BT.601/709/2020, limited or full
  range; HDR10 and HLG tone mapped as the Direct3D11 shader does).
- The layer is composed like an uploaded frame and kept as the retained background.
- FFmpeg's timeline semaphores and frame lock order the read after the decode.
- Without a decode queue (lavapipe, older drivers) the probe asks for software decoding and
  says why (`no Vulkan Video decoding: ...`).
- FFmpeg must be built with `--enable-vulkan` (FFmpeg 9, libavutil 61; the structure layouts
  are pinned by `VulkanVideoDecodeTests`).

## Surfaces

Any number of surfaces can be attached (Stage 027A slice 5: a primary plus secondary screens),
each a managed frame sink or a shared GPU image consumer, and each composition is rendered once
per surface at that surface's size. Shared images are exported opaque-fd images on Linux (Stage
027A slice 2). On Windows (Stage 045A slice 3) they are keyed-mutex Direct3D11 textures, which
the renderer creates on its device's adapter and imports into Vulkan, for Avalonia's default
ANGLE compositor; or opaque NT-handle images with NT-handle semaphores for Avalonia's Vulkan
backend.

## Shaders

GLSL sources live in `Shaders/*.vert|*.frag`. The compiled SPIR-V (`*.spv`) is committed and
embedded into the assembly because CI machines do not have glslang. After editing a shader,
run `scripts/compile-vulkan-shaders.sh` (needs `glslangValidator`, Debian/Ubuntu package
`glslang-tools`) and commit the regenerated `.spv` files.
