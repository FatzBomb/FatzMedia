# Stage Update

Stage: 026A.1 - Vulkan vertical slice (offscreen clear + BGRA background)
Date: 2026-10-16

## Status: IMPLEMENTED - validated headlessly on lavapipe; real-GPU smoke pending

Delivered:

- `FatzBomb.MediaControl.Rendering.Vulkan`: `VulkanOffscreenCompositor` (instance,
  device, queue, command buffer, staging/readback buffers, BGRA8 images),
  `VulkanCompositionPlanner` (clear policy, destination rounding, crop), internal
  `VulkanRendererBackend : IMediaRendererBackend`, public
  `VulkanPresentationCoordinatorFactory`.
- `FatzBomb.MediaControl.Rendering.Vulkan.Tests`: 16 cases - planner math plus GPU
  conformance against `SoftwareCompositionRenderer`.
- CI: the Linux job installs Mesa lavapipe and runs the Vulkan conformance suite.
- Renderer capability/mapping matrix in the stage file.

## Validation (Linux sandbox, llvmpipe Vulkan 1.4)

- Clear-only and integral placements (UniformFit letterbox, Fill, Original, 16:9 fit)
  match the software renderer pixel-for-pixel.
- Fractional placements match >= 75 % exactly (observed 80 %); remaining rows/columns
  differ by one source pixel because blits take integer destination edges. Shader
  sampling in slice 2 removes the difference.
- With no ICD (`VK_ICD_FILENAMES=/nonexistent.json`) every test degrades without a crash.

## Decisions made without the owner

- Silk.NET.Vulkan 2.22 bindings (MIT), referenced only by the Vulkan package.
- Slice 1 targets the managed frame-sink surface only; window swapchains are slice 2.
- Unsupported frame kinds are rejected (return `false` + diagnostic) rather than drawn
  partially.
- Straight-alpha backgrounds are blitted as-is (opaque video/images are unaffected);
  premultiplication moves into the shader slice.

## Manual smoke (any OS with a Vulkan GPU driver)

1. Run `dotnet test FatzBomb.MediaControl.Rendering.Vulkan.Tests` on a machine with a
   real GPU: the diagnostic device name is the GPU (not llvmpipe) and all 16 pass.
2. In a host with a frame-sink surface (for example the WPF-composited surface mode),
   swap `SoftwarePresentationCoordinatorFactory` for
   `VulkanPresentationCoordinatorFactory`: video plays with correct letterboxing;
   karaoke/CDG frames log the one-time "cannot draw this frame yet (Overlays)" warning.
