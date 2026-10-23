# Active Stage 027A - UI Adapter Implementations

Prepared: 2026-10-16
Status: Avalonia (slice 1) and WinForms (slice 3) implemented. Avalonia GPU-shared surface
(slice 2) implemented 2026-10-23, pending manual smoke (`updates/027A_2`); WinUI 3 (slice 4) is
deferred. Order changed from the idea (Avalonia first) per the owner's session instructions.

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
- **Slice 2 - Avalonia GPU-shared surface (implemented 2026-10-23, pending manual smoke;
  `updates/027A_2`):**
  - Hardware-rendered frames are shown in Avalonia's own compositor through its GPU interop:
    `ICompositionGpuInterop` imports into a `CompositionDrawingSurface` on a
    `CompositionSurfaceVisual`, with `UpdateWithKeyedMutexAsync` /
    `UpdateWithSemaphoresAsync` / `UpdateAsync`. There is no CPU copy and no overlay window.
  - Core: the surface type `shared-gpu-image`. The adapter registers an
    `IMediaSharedGpuImageConsumer` through `MediaSharedGpuImageConsumerHandle`, and its
    `MediaSharedGpuImageRequest` lists the handle and sync kinds the compositor imports
    and the compositor's GPU LUID/UUID.
  - The renderer negotiates (`TrySelect`, `MatchesDevice`) and creates a double-buffered
    `MediaSharedGpuImageSet` (plain handles, size, format, sync).
  - It announces each finished frame with `OnFrameReady`, and the consumer gives every
    frame back through `ReleaseFrame`. `MediaSharedGpuImageRing` keeps the producer side:
    buffer rotation, keyed-mutex keys, and pending semaphore waits.
  - A renderer that cannot serve the consumer calls `OnUnavailable(reason)`. Core also does
    this for any renderer that rejects the surface, including deferred re-attaches.
  - Direct3D11: a `SharedTexture` primary backend. Two BGRA textures with
    `SharedKeyedMutex`, as NT or global handles; the composed target is copied in under
    the mutex (producer key 0 to 1, compositor key 1 to 0). The adapter LUID must match.
  - Vulkan: two RGBA8 dedicated images exported as opaque POSIX fds
    (`VK_KHR_external_memory_fd`), plus exported binary semaphores when
    `VK_KHR_external_semaphore_fd` is present. Host synchronization always works because
    the fence completes each frame. The device UUID must match. On Windows it is not
    implemented yet.
  - The Avalonia adapter picks the path at attach (`PreferGpuSharedSurface`, default
    true). Its conditions are: the compositor's interop, a supported handle kind, and the
    renderer accepting.
  - Otherwise, or after any later failure, the adapter falls back to the frame sink. It
    logs the path and the reason (Info, `Avalonia.MediaSurface`) and exposes them
    (`PresentationPath`, `PresentationPathReason`). It still references only Core and
    handles, so conformance item 3 holds.
  - Deferred: Vulkan export on Windows; choosing the renderer GPU from the compositor's
    identity (a mismatch falls back today); shared images for secondary targets; D3D12
    shared fences; desktop validation of the new paths (manual smoke).

  This replaces the earlier plan of `NativeControlHost` HWND/X11 child windows for
  swapchain renderers. A native child window inside Avalonia has the same airspace
  limitation as WPF's `NativeHwndOverlay`, so overlays would need a separate window there
  too. The compositor import avoids that.
- **Slice 3 - WinForms (implemented):** `FatzBomb.MediaControl.WinForms` HWND
  `MediaSurface` (Core only; pairs with Windows + Direct3D11).
- **Slice 4 - WinUI 3 (deferred):** `SwapChainPanel` or HWND interop surface. Deferred
  because the Windows App SDK XAML toolchain cannot be built or validated in the Linux
  session and would add an unvalidated Windows-only build dependency to CI.

## Explicit Non-Goals

As in the original idea: no shared cross-framework base control, no player/playlist UI,
no renderer/pipeline/GPU references, not every framework in one stage.
