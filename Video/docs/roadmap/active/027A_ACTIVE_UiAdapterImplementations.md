# Active Stage 027A - UI Adapter Implementations

Prepared: 2026-10-16
Status: Avalonia slice implemented and validated headlessly on Linux. WinForms and WinUI 3
slices follow (see Slices). Order changed from the idea (Avalonia first) per the owner's
session instructions.

## Framework-Adapter Conformance Checklist (extracted from WPF, pinned)

1. Own the control lifecycle: attach the primary surface when the control is in the visual
   tree, has positive size, and has a session; detach when removed or when the session
   changes.
2. Pass a `MediaPresentationSurfaceDescriptor` (Primary id/role, neutral native payload,
   pixel size = layout size x DPI scaling) to `AttachPresentationSurface`; resize through
   `ResizePresentationSurface` only when the pixel size changes.
3. Never reference renderer, pipeline, internal CDG, video-frame, or GPU types; never
   reference renderer projects (enforced by `scripts/check-adapter-boundary.ps1`).
4. Forward only presentation preferences (`VideoDisplayMode`) to the session.
5. Expose framework source conversions only (bitmap -> `MediaInputSource`).
6. Present frames arriving on background threads through the framework dispatcher;
   latest frame wins under bursts.
7. No blocking session calls on UI event paths (attach/resize/detach are non-blocking).

## Slices

- **Slice 1 - Avalonia (implemented):** `FatzBomb.MediaControl.Avalonia` (net10.0,
  Avalonia 12.1.4, Core only): composited `MediaSurface` over the managed frame-sink
  contract and `AvaloniaMediaInputSources.FromBitmap`. Works with any platform factory
  (Windows, Linux) and any frame-sink renderer (software, Vulkan preview).
- **Slice 2 - Avalonia native-surface mode:** `NativeControlHost` HWND/X11 descriptors for
  swapchain renderers (needs 026A swapchain slice).
- **Slice 3 - WinForms:** HWND `MediaSurface` over Windows + Direct3D11.
- **Slice 4 - WinUI 3:** `SwapChainPanel` or HWND interop surface.

## Explicit Non-Goals

As in the original idea: no shared cross-framework base control, no player/playlist UI,
no renderer/pipeline/GPU references, not every framework in one stage.
