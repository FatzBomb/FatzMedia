# Active Stage 026A - Cross-Platform Renderer Backends

Prepared: 2026-10-16
Status: Vulkan vertical slice 1 implemented and validated headlessly on Mesa lavapipe
(Linux sandbox + Linux CI). Swapchains, shader composition, OpenGL, and Metal are
later slices.

## `IMediaRendererBackend` Mapping (pinned)

| Contract concern | Direct3D11 (reference) | Vulkan | OpenGL | Metal |
|---|---|---|---|---|
| Surface types | Win32 HWND swap chain; managed frame sink | slice 1: managed frame sink (offscreen + readback); next: `VkSurfaceKHR` from Win32 HWND / Xlib / Wayland descriptors | EGL/GLX/WGL window surface; FBO + readback for frame sink | `CAMetalLayer` from NSView/UIView descriptor; texture readback for frame sink |
| Swap chain | DXGI flip model, Direct3D11 present options | `VkSwapchainKHR`, FIFO/MAILBOX from renderer options | default framebuffer + swap interval | `CAMetalLayer.nextDrawable`, `displaySyncEnabled` |
| Software BGRA upload | dynamic texture | staging buffer -> optimal image (`vkCmdCopyBufferToImage`) | `glTexSubImage2D` (PBO later) | `MTLBuffer` -> `MTLTexture` blit |
| Composition | D3D11 shader passes (background, wash, CDG, subtitle, transform) | slice 1: clear + nearest `vkCmdBlitImage` background; next: graphics pipeline with SPIR-V shaders mirroring the D3D11 passes | GLSL passes | MSL passes |
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
  backgrounds are rejected with one diagnostic per kind (never drawn incorrectly).
- `VK_ERROR_DEVICE_LOST` releases the device; the next present re-initializes.

## Next Slices

1. Window-system swapchains (Win32 HWND first, then Xlib/Wayland) and resize.
2. SPIR-V graphics pipeline: exact fractional sampling, wash, CDG, subtitles, transforms,
   straight-alpha backgrounds.
3. Multi-target fan-out and present-mode pacing options.
4. OpenGL and Metal packages reusing the conformance suite.

## Explicit Non-Goals

As in the original idea: no all-renderers stage, no preemptive Core contract changes, no
OS/UI ownership in renderers, no automatic cross-renderer fallback, no GPU dependency in
Core.
