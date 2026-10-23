# Stage Update

Stage: 027A.2 - Avalonia GPU-shared surface (compositor import, no CPU copy)
Date: 2026-10-23

## Status: IMPLEMENTED - pending manual smoke

The Avalonia `MediaSurface` can now show renderer frames as shared GPU images that Avalonia's
own compositor imports (`ICompositionGpuInterop`). There is no readback, no `WriteableBitmap`
upload, and no overlay window. When the compositor or the renderer cannot do this, the surface
uses the slice 1 frame sink as before. It also falls back if the GPU path fails later.

Delivered:

- **Core contract** (`Api/MediaSharedGpuImage*.cs`, plain values and OS handles only):
  - surface type `shared-gpu-image` with handle `shared-image-consumer`, an opaque token from
    `MediaSharedGpuImageConsumerHandle` (the same pattern as the frame-sink token);
  - `IMediaSharedGpuImageConsumer`, implemented by the adapter: `Request`, `OnImagesCreated`,
    `OnFrameReady`, `OnUnavailable`;
  - `MediaSharedGpuImageRequest` (what the consumer imports): handle kinds in preference
    order with their sync kinds, semaphore handle kinds, and the GPU's LUID/UUID. It has
    `TrySelect` (negotiation) and `MatchesDevice`;
  - `MediaSharedGpuImageSet` (created by the renderer): handle kind, sync kind, size,
    format, the image buffers (memory handle, size, offset, semaphore handles), and the
    Vulkan layout. `ReleaseFrame` and `CompleteImport` go back to the renderer, and
    `Retire` is called by the renderer;
  - `MediaSharedGpuImageRing`: producer bookkeeping shared by both renderers (buffer
    rotation, keyed-mutex keys, pending semaphore waits);
  - handle kinds: D3D11 NT handle, D3D11 global (KMT) handle, Vulkan opaque fd, and Vulkan
    opaque NT handle. Sync kinds: keyed mutex, semaphores, host-synchronized;
  - `MediaPresentationCoordinator` calls `OnUnavailable` when the renderer rejects a
    shared-image surface. This also covers deferred attaches: device re-creation, and a
    graph rebuilt by a mode switch.
- **Direct3D11** (`Presentation/Shared/D3D11SharedTextureBackend.cs`): a new primary backend
  kind, `SharedTexture`. It sits beside the swap-chain, DirectComposition, and managed
  frame-sink backends, and like the managed sink it is the only target while it is active.
  - The renderer composes into a private target as always. `Present` copies it on the GPU
    into one of two `SharedKeyedMutex` (+ `SharedNTHandle`) BGRA textures, under that
    texture's keyed mutex.
  - It offers NT handles and legacy global handles. Whichever the compositor prefers wins
    (NT first).
  - It compares the adapter LUID with the compositor's. On a mismatch (WARP, the other GPU
    of a hybrid laptop) it declines with that reason.
  - It reads `AcquireSync` through the vtable, because Vortice's wrapper hides
    `WAIT_TIMEOUT` and `WAIT_ABANDONED`. On either, it re-exports a fresh pair.
  - It closes NT handles once both sides are done with the set.
- **Vulkan** (`VulkanOffscreenCompositor.Export.cs`, `VulkanRendererBackend`):
  - When the device has `VK_KHR_external_memory_fd`, the renderer exports two RGBA8 images
    with dedicated memory as opaque POSIX fds. A blit from the BGRA target converts the
    channels.
  - With `VK_KHR_external_semaphore_fd` it also exports a render-complete and a
    consumer-release binary semaphore per image.
  - Every frame finishes on the GPU (fence) before it is announced, so it also offers
    host synchronization.
  - Images are left in `TransferSrcOptimal`, and importers are told that layout. The
    renderer checks the device UUID against the compositor's.
  - The readback path is unchanged.
- **Avalonia adapter**:
  - `MediaSurface` gains `PreferGpuSharedSurface` (default true), `PresentationPath`
    (`None`/`FrameSink`/`SharedGpuImage`), `PresentationPathReason`, and
    `PresentationPathChanged`. It raises an Info diagnostic (`Avalonia.MediaSurface`) for
    every path decision and every fallback reason.
  - Internal `AvaloniaSharedImageSurface` (the consumer):
    - `CompositionSurfaceVisual` + `CompositionDrawingSurface` as the control's child
      visual;
    - imports through `ICompositionGpuInterop.ImportImage`/`ImportSemaphore`;
    - submits every frame in order with `UpdateWithKeyedMutexAsync` /
      `UpdateWithSemaphoresAsync` / `UpdateAsync`, and releases each frame when the update
      task completes;
    - disposes the imports and the drawing surface only after their updates finish.
  - `AvaloniaGpuInterop` / `GpuInteropCapabilities` map Avalonia's handle-type strings and
    sync capabilities to the Core enums. The adapter still references no renderer project,
    graphics API, or GPU type (conformance item 3; `check-adapter-boundary.ps1` passes).
- **Avalonia test player**:
  - `--surface=auto|framesink`;
  - the status line ends with `via shared GPU image` or `via frame sink`, and the log
    shows the path and reason;
  - the Windows build offers the **Direct3D11** renderer (`--renderer=direct3d11`) through
    a new Windows-only reference to `FatzBomb.MediaControl.Windows.Direct3D11`. The DAG in
    `check-core-boundary.ps1` is updated: the host composition root needs a renderer that
    exports DXGI shared textures, and before this the Avalonia player had only frame-sink
    renderers.

Tests (new or extended):

- Portable/Windows (`FatzBomb.MediaControl.Tests`, linked into Portable.Tests):
  - `MediaSharedGpuImageRingTests` (9): alternation, waiting for release, consumed versus
    unused keys, pending semaphore waits, least-recently-presented order, stale
    generations, abort, cross-thread wake-up, timeout;
  - `MediaSharedGpuImageRequestTests` (5);
  - `MediaSharedGpuImageSetTests` (3);
  - `MediaSharedGpuImageConsumerHandleTests` (2);
  - 2 coordinator tests (a rejected shared-image surface tells its consumer; a rejected
    window surface does not).
- Windows only: `D3D11SharedTextureBackendTests` (4): LUID bytes, negotiation with what
  Avalonia/ANGLE offers, no match for a descriptor-only compositor, an inactive backend in
  the set.
- Avalonia headless: `SharedGpuImagePathTests` (9):
  - the real headless compositor has no interop, so the frame-sink fallback is used and
    its reason given;
  - the opt-out;
  - with fake interop capabilities: the shared-image attach and its request, the
    renderer's rejection reason, a later `OnUnavailable` re-attaching the frame sink
    (sticky for the session), unimportable images handing the handles back, a pending
    interop query, unsupported handle types;
  - handle-type mapping.
- Vulkan on lavapipe: `VulkanSharedImageTests` (5):
  - **export, then re-import on a second `VkDevice`, and read back the composed colour**;
  - the backend's double-buffered export with host sync: alternation, a drop when both
    images are held, release, re-import of the announced image, resize to a new
    generation, retire on detach;
  - declining a consumer that imports only D3D11 handles, and one on another GPU (UUID);
  - capabilities.

## Sync design

- **Keyed mutex (D3D11).** Each image has a mutex.
  - The producer acquires key 0, copies, and releases key 1. The consumer (Avalonia's
    `UpdateWithKeyedMutexAsync(image, 1, 0)`) acquires 1, reads, and releases 0.
  - The ring hands the producer only an image whose previous frame the compositor has
    released (its update task completed). So the producer never waits on the mutex in
    steady state, and the compositor never samples a half-written frame.
  - If an image comes back unused, its mutex is still at key 1, and the ring tells the
    producer to acquire key 1 instead.
- **Semaphores (Vulkan, when exportable).** The producer's submit waits on the image's
  consumer-release semaphore (only when the compositor signalled it, which the ring
  tracks) and signals render-complete. The compositor's `UpdateWithSemaphoresAsync` waits
  on render-complete and signals consumer-release. An unused frame would leave
  render-complete signalled, so the renderer starts a fresh set instead.
- **Host-synchronized (Vulkan on lavapipe, or wherever the compositor reports
  `Automatic`).** The fence completes the GPU work before the frame is announced, and the
  update task's completion releases the image.
- **Back-pressure.** With both images held by the compositor, a frame waits up to 50 ms for
  one, then is dropped. It is not reported as a present failure, and a diagnostic is
  raised once per set. This is latest-wins under bursts (conformance item 6): the producer
  can never get more than two frames ahead.

## What works where

| Platform / Avalonia backend | Direct3D11 renderer | Vulkan renderer | Software renderer |
|---|---|---|---|
| Windows, Avalonia default (ANGLE on D3D11) | **Shared GPU image** (NT handle + keyed mutex), same adapter only | frame sink (Vulkan export on Windows deferred; reason logged) | frame sink |
| Windows, Avalonia Vulkan backend | frame sink (no common handle kind) | frame sink (deferred) | frame sink |
| Linux X11/Wayland, Avalonia OpenGL (EGL with `GL_EXT_memory_object_fd`/`GL_EXT_semaphore_fd`) | n/a | **Shared GPU image** (opaque fd + semaphores) on Mesa radv/anv/iris and NVIDIA | frame sink |
| Linux, Avalonia Vulkan backend | n/a | **Shared GPU image** (opaque fd + semaphores) | frame sink |
| Linux on lavapipe (no semaphore export) | n/a | frame sink (no common sync: the compositor needs semaphores), unless it reports `Automatic` | frame sink |
| Headless / software Skia | n/a | frame sink (no GPU interop) | frame sink |

The bold cells are designed and unit-tested, but nobody has run them on real desktop
compositors yet: the Linux sandbox has no display GPU. The Vulkan export itself (allocation,
fd export, re-import on another device, pixel content) is validated on lavapipe.

## Decisions made without the owner

- Closed enums for handle and sync kinds, not extensible string ids: each kind carries
  ownership and synchronization semantics that both sides must implement.
- The adapter decides at attach. If `TryGetCompositionGpuInterop` has not answered yet, the
  attach waits for it, so the path is chosen once and never switched mid-attach. A failure
  later (an import fault, an update fault, a renderer `OnUnavailable`) falls back to the
  frame sink. The fallback sticks for that session until the `Player` changes or
  `PreferGpuSharedSurface` is toggled.
- Diagnostics from the adapter are not owned by a player (the adapter only has
  `IMediaControlSession`), so every player's `MediaDiagnostic` shows them, prefixed
  `Surface #n`. Per-surface state is on `PresentationPath*`.
- D3D11 copies the composed frame into the shared texture (one GPU copy). Rendering
  straight into it would need the composition target to change per frame.
- Vulkan exports RGBA8, because OpenGL importers have no BGRA storage format. The blit
  swizzles.
- Vulkan export on Windows (opaque NT handles), queue-family ownership transfer to
  `VK_QUEUE_FAMILY_EXTERNAL`, and picking the compositor's GPU when it differs from the
  renderer's are deferred. On a mismatch the renderer declines and the frame sink is used.
- The Avalonia player decodes in software for every renderer (as before), so a CPU
  comparison isolates the presentation path.

## Deferred

- Vulkan export on Windows (`VK_KHR_external_memory_win32` + semaphores) for Avalonia's
  Vulkan backend on Windows.
- Selecting the renderer GPU from the compositor's LUID/UUID. Today a mismatch falls back.
- Shared images for secondary/projector targets (primary only, like the managed sink).
- DXGI shared fences (D3D12 fence NT handles) as an alternative to keyed mutexes.
- Desktop validation of the bold table cells (manual smoke below).

## Verification (Linux sandbox)

- `EnableWindowsTargeting=true dotnet build FatzBomb.MediaControl.sln -c Release`: 0
  warnings, 0 errors.
- Tests: Portable 1085, Linux 51, Avalonia (headless) 23, Vulkan (lavapipe) 57. All pass.
- `dotnet format --verify-no-changes`: clean. `check-core-boundary`,
  `check-adapter-boundary`, `check-presentation-parity`, and `check-wpf-boundary` pass.
- The Windows test project (including `D3D11SharedTextureBackendTests`) builds; it runs on
  Windows CI.

## Manual smoke (Windows, native DLLs in `FatzBomb.MediaControl.Player.Avalonia/native/`)

Run `dotnet run --project FatzBomb.MediaControl.Player.Avalonia -c Release -f net10.0-windows7.0 -- <video> --renderer=direct3d11 ...`

1. **Path selection.** Run with `--surface=auto`.
   - The status line ends `via shared GPU image`.
   - The log shows `Surface #1: shared GPU image path (the compositor imports
     D3D11TextureNtHandle [KeyedMutex], ...)` and `Shared textures exported: #n WxH
     Bgra8Unorm x2: D3D11TextureNtHandle, KeyedMutex`.
   - If it says frame sink instead, note the reason in the log (for example a LUID
     mismatch on a hybrid laptop).
2. **Frame-sink baseline.** Run the same file with `--surface=framesink`. The status line
   ends `via frame sink`, with reason `PreferGpuSharedSurface is off`.
3. **CPU.** Play the same 1080p (and, if available, 4K) file in each mode for 30 s. Compare
   the status line's CPU % and cores used. Shared GPU image should be clearly lower,
   because it has no readback, no bitmap copy, and no upload.
4. **Tearing and ordering.** Play fast motion (a panning shot) and a CDG/karaoke file in
   auto mode. There must be no torn frames, no flicker between old and new frames, and no
   black frames during playback. Pause and seek: the shown frame updates and stays put.
5. **Resize.** Drag the window edge continuously, maximize and restore, and move it between
   monitors of different DPI. Video keeps rendering at the new size, with no stretched
   stale frame for more than a moment, no crash, and no `D3D11.DeviceLost`. The log shows a
   new `Shared textures exported` line per size.
6. **Several players.** Run `--players=2`, then `--players=4`, in auto mode, each player
   with a video. All show `via shared GPU image` and play smoothly. Compare the CPU with
   the same count in `--surface=framesink`.
7. **Fallbacks.**
   - `--renderer=vulkan` and `--renderer=software` show `via frame sink`, with a reason.
     For Vulkan it is `Vulkan image export on Windows ... not implemented yet`; for
     software it is `the Software renderer did not accept the shared GPU image surface`.
   - Changing the renderer in the toolbar re-creates the players with the right path.
8. **Mode switch.** In auto mode, switch a player to Audio and back to Full with the Mode
   box. Video returns on the shared path, or on the frame sink with a logged reason. It is
   never a blank surface.

Linux (desktop with a GPU, optional):

- `dotnet run ... -f net10.0 -- <video> --renderer=vulkan --surface=auto`. On Mesa or
  NVIDIA with Avalonia's default OpenGL backend, the status line should read `via shared
  GPU image`, with `VulkanOpaqueFd, Semaphores (VulkanOpaqueFd)` in the log.
- Repeat steps 2 to 6. On lavapipe or under Xvfb, the frame sink with a logged reason is
  expected.
