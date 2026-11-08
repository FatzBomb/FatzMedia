# Active Stage 026A - Cross-Platform Renderer Backends

Prepared: 2026-10-16
Status: Vulkan slices 1 (clear + background blit) and 2 (SPIR-V wash + CDG overlay)
implemented, pending manual smoke; validated headlessly on Mesa lavapipe (Linux sandbox +
Linux CI). Swapchains, subtitles, transforms, OpenGL, and Metal are later slices.

## `IMediaRendererBackend` Mapping (pinned)

| Contract concern | Direct3D11 (reference) | Vulkan | OpenGL | Metal |
|---|---|---|---|---|
| Surface types | Win32 HWND swap chain; managed frame sink | slice 1: managed frame sink (offscreen + readback); next: `VkSurfaceKHR` from Win32 HWND / Xlib / Wayland descriptors | EGL/GLX/WGL window surface; FBO + readback for frame sink | `CAMetalLayer` from NSView/UIView descriptor; texture readback for frame sink |
| Swap chain | DXGI flip model, Direct3D11 present options | `VkSwapchainKHR`, FIFO/MAILBOX from renderer options | default framebuffer + swap interval | `CAMetalLayer.nextDrawable`, `displaySyncEnabled` |
| Software BGRA upload | dynamic texture | staging buffer -> optimal image (`vkCmdCopyBufferToImage`) | `glTexSubImage2D` (PBO later) | `MTLBuffer` -> `MTLTexture` blit |
| Composition | D3D11 shader passes (background, wash, CDG, subtitle, transform) | slice 1: clear + nearest `vkCmdBlitImage` background; slice 2: SPIR-V render pass for wash and CDG overlay; next: subtitle, transform, and shader-sampled background passes | GLSL passes | MSL passes |
| Native video resources | D3D11 textures from FFmpeg D3D11VA | `VK_KHR_video_decode_*` or DMA-BUF import (later) | EGLImage/DMA-BUF (later) | `CVPixelBuffer` -> `MTLTexture` (later) |
| Subtitle rasterization | Direct2D/DirectWrite | platform-neutral rasterizer (Skia/HarfBuzz) - shared with GL/Metal | same | CoreText or the shared rasterizer |
| Pacing | Core `MediaPresentationCoordinator` + DXGI waitable | Core coordinator + present mode / `VK_KHR_present_wait` | Core coordinator + swap interval | Core coordinator + `CADisplayLink` |
| Multi-target fan-out | one swap chain per target, one composition plan | one swapchain per target sharing one device and plan | one context per window or shared FBO blit | one layer per target |
| Device loss | `DXGI_ERROR_DEVICE_REMOVED` -> recreate | `VK_ERROR_DEVICE_LOST` -> backend cleanup, Core re-initializes (slice 1 implements this path) | context loss (robustness ext) | `MTLDevice` removal notification |

## Slice 1 (implemented)

- Package `FatzBomb.MediaControl.Rendering.Vulkan` (net10.0, Core only, Silk.NET.Vulkan).
- `VulkanPresentationCoordinatorFactory` (host-selectable like the software factory) and
  `IsSupported(out reason)` probe.
- Device selection: discrete > integrated > virtual > CPU, requiring BGRA8 blit/transfer
  support on a graphics queue.
- Per frame: GPU clear (software clear policy, premultiplied) -> optional background
  upload and nearest blit with the software renderer's destination rounding and a
  proportional source crop -> readback into the managed frame sink.
- Frames needing wash, CDG, subtitles, base-visual transforms, native or retained
  backgrounds were rejected with one diagnostic per kind (never drawn incorrectly); slice 2
  draws wash and CDG.
- `VK_ERROR_DEVICE_LOST` releases the device; the next present re-initializes.

## Slice 2 (implemented)

- SPIR-V graphics pipeline (`VulkanOverlayPipelines`): one render pass (load/store) on the
  BGRA8 target after the clear and blit, premultiplied ONE / ONE_MINUS_SRC_ALPHA blending.
  GLSL in `Shaders/`, committed SPIR-V embedded in the assembly,
  `scripts/compile-vulkan-shaders.sh` regenerates it.
- Wash: full-target quad, straight `WashArgb` premultiplied in the shader.
- CDG overlay: palette indices expanded on the CPU to straight BGRA (background index and
  out-of-range indices transparent, other entries forced opaque), uploaded as a sampled image,
  drawn nearest/clamp-to-edge over the clipped UniformFit rectangle with texture coordinates
  from the pure planner (`VulkanCompositionPlanner.PlanOverlay`, the software mapping).
- Classification: only subtitles (`Subtitles`), base-visual transforms, native/retained
  backgrounds, and invalid CDG snapshots are rejected.
- Device selection additionally requires colour-attachment, blend, and sampled-image support
  for B8G8R8A8Unorm; the failure names each rejected device and its missing features.
- Conformance: wash within +/-1 per channel (fixed-function blend rounding), CDG exact except
  at texel-edge scales (1.5x, 0.5x), where it stays within one source texel. See
  `updates/026A_2`.

## Shared Image Export (Stage 027A slice 2)

The Vulkan renderer also accepts the `shared-gpu-image` surface. It blits each composed frame
into one of two exported RGBA8 images (opaque POSIX fds, plus exported semaphores when the
device has them) for the Avalonia compositor to import, instead of reading it back. Validated
on lavapipe: the export is re-imported on a second device. See `updates/027A_2`.

## Next Slices

1. Window-system swapchains (Win32 HWND first, then Xlib/Wayland) and resize.
2. Remaining shader passes: subtitles (premultiplied, canvas placement), base-visual
   transforms, and shader-sampled straight-alpha backgrounds (exact fractional placement).
3. Multi-target fan-out and present-mode pacing options.
4. OpenGL and Metal packages reusing the conformance suite.

## Explicit Non-Goals

As in the original idea: no all-renderers stage, no preemptive Core contract changes, no
OS/UI ownership in renderers, no automatic cross-renderer fallback, no GPU dependency in
Core.
