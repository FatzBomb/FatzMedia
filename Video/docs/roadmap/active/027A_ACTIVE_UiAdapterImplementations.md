# Active Stage 027A - UI Adapter Implementations

Prepared: 2026-10-16
Status: Avalonia (slice 1) and WinForms (slice 3) implemented. Avalonia GPU-shared surface
mode (slice 2, re-planned 2026-10-18) is not started; WinUI 3 (slice 4) is deferred. Order changed from the idea (Avalonia first) per the owner's
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
- **Slice 2 - Avalonia GPU-shared surface mode (re-planned 2026-10-18):** hardware-rendered
  frames shown in Avalonia's own compositor through its GPU interop
  (`ICompositionGpuInterop` on a `CompositionDrawingSurface`, Avalonia 11+; the repo is on
  12.1.4), with no CPU copy and no overlay window. Shape:
  - Core: a new neutral surface payload type, "shared GPU image" (plain handles, size,
    format, sync handle), alongside the HWND and managed frame-sink payloads; no GPU types.
  - Renderers export the composed frame as a shared image with a sync primitive so the
    compositor never samples a half-written frame: Direct3D11 a DXGI shared texture with a
    keyed mutex (or shared fence); Vulkan exportable memory (opaque fd / NT handle) plus a
    semaphore. Double-buffered, so rendering and composition overlap.
  - The Avalonia adapter imports the handles and shows them; it still references no
    renderer or GPU types (handles only, conformance item 3 holds). It checks the
    compositor's supported handle types at attach and falls back to the existing frame-sink
    mode when the active Avalonia backend cannot import them.
  - Validation: Windows (Avalonia on its Direct3D11 backend) and Linux (Avalonia on Vulkan),
    manual; parity checks against the frame-sink mode.

  Replaces the earlier plan (`NativeControlHost` HWND/X11 child windows for swapchain
  renderers): a native child window inside Avalonia has the same airspace limitation as
  WPF's `NativeHwndOverlay`, so overlays would need a separate window there too, which the
  compositor import avoids. Depends on 026A for the Vulkan export; the Direct3D11 export is
  independent of it.
- **Slice 3 - WinForms (implemented):** `FatzBomb.MediaControl.WinForms` HWND
  `MediaSurface` (Core only; pairs with Windows + Direct3D11).
- **Slice 4 - WinUI 3 (deferred):** `SwapChainPanel` or HWND interop surface. Deferred
  because the Windows App SDK XAML toolchain cannot be built or validated in the Linux
  session and would add an unvalidated Windows-only build dependency to CI.

## Explicit Non-Goals

As in the original idea: no shared cross-framework base control, no player/playlist UI,
no renderer/pipeline/GPU references, not every framework in one stage.
