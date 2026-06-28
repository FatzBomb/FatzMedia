# FatzBombMedia Renderer Current Architecture

_Last updated after Stage 7G._

## Purpose

This document describes the current renderer split after the Stage 6/7 refactor. It is intended as a maintenance map for the MediaControl renderer so future changes do not re-entangle CDG, background media, presentation backends, and WPF/native surface concerns.

## Current high-level shape

```text
MediaPlayer / MediaRenderer
  -> OptimizedGraphicsRenderer
      -> host-level render orchestration
      -> background/CDG/session state coordination
      -> delegates specialized rendering paths
  -> VorticeD3D11Renderer
      -> thin compatibility facade over D3D11 presentation services
      -> public API compatibility for existing callers
```

## Key rule

Presentation backend details should stay below `VorticeD3D11Renderer` and `D3D11RendererPresentationServices`.

Host/session decisions can live in `OptimizedGraphicsRenderer`, but low-level D3D target ownership, resize handling, frame upload cache ownership, DirectComposition overlay upload, and active target validation should not move back into it.

---

## VorticeD3D11Renderer boundary

`VorticeD3D11Renderer` is now a thin compatibility facade.

It should mostly do:

```text
public API
lock
basic disposed guard
delegate to D3D11RendererPresentationServices
raise FramePresented
dispose
```

It should not directly own:

```text
swap-chain target bookkeeping
active presentation texture validation
native HWND resize adoption
DirectComposition overlay upload internals
CPU upload cache lifetime
render target view cache lifetime
layer compositor lifetime
presentation cleanup ordering
```

Those responsibilities are now handled by presentation services and helper classes.

---

## D3D11 presentation service graph

`Core/Graphics/D3D/Presentation/D3D11RendererPresentationServices.cs` builds and owns the D3D presentation-side service graph.

Important collaborators:

| Class | Responsibility |
|---|---|
| `D3D11DeviceResources` | D3D11 device/context and common device resources. |
| `D3D11VideoProcessorPresenter` | Hardware/video-processor blit support. |
| `D3D11FrameUploadPipeline` | CPU frame upload into a presentation texture or upload texture. |
| `D3D11FrameUploadResources` | CPU upload texture cache and upload diagnostics routing. |
| `D3D11PresentationBackendSet` | Backend collection for D3DImage, native HWND, and DirectComposition. |
| `D3D11PresentationSurfaceController` | Active target state, surface creation, resize, DirectComposition upload routing, and validated target snapshots. |
| `D3D11PresentationTargetSnapshot` | Consistent target texture/size/backend snapshot after resize handling. |
| `D3D11PresentationState` | Native stretch and live-resize state. |
| `D3D11PresentationBlitter` | Blits source textures into the active presentation target. |
| `D3D11PresentationFramePresenter` | CPU/hardware frame handoff through validated target snapshots. |
| `D3D11LayerCompositor` | Retained overlay/wash composition resources. |
| `D3D11PresentationRenderTargetViewCache` | Optional retained-layer render-target-view lifetime. |
| `D3D11PresentationResourceCleaner` | Cleanup ordering for surfaces/resources. |

### Resize safety rule

Frame upload and hardware-frame presentation should use `D3D11PresentationTargetSnapshot` instead of independently reading target texture, width, and height.

Correct order:

```text
apply pending native HWND resize if requested
get validated active target snapshot
use snapshot texture/width/height for this frame handoff
```

This prevents stale/null presentation texture usage during live resize.

---

## Presentation backends

Current backends:

| Backend | Class | Notes |
|---|---|---|
| WPF `D3DImage` | `D3D11D3DImagePresentationBackend` | Shared-surface bridge for WPF-hosted rendering. |
| Native HWND swap-chain | `D3D11NativeHwndSwapChainBackend` | Strict child-HWND DXGI swap-chain target. |
| DirectComposition layered target | `D3D11DirectCompositionSingleTargetBackend` | Base + transparent overlay swap-chain path. |

DirectComposition overlay upload is backend-owned. The backend owns overlay copy/blit/present/commit behavior.

---

## OptimizedGraphicsRenderer boundary

`OptimizedGraphicsRenderer` remains the host-level coordinator. The active CDG path is DXCDG, not the retired bitmap/overlay pipeline.

Current helper classes:

| Class | Responsibility |
|---|---|
| `OptimizedPresentationRouter` | Chooses and sizes active D3DImage/native HWND/DirectComposition presentation paths. |
| `NativeCompositePresentationScheduler` | Coalesces static/native composite presentation requests and dirty-state timing for non-CDG base layers. |
| `NativeCompositeBaseState` | Stores native composite base frame/state for image/video backgrounds. |
| `CpuWriteableBitmapRenderer` | Owns WPF `WriteableBitmap` fallback rendering for non-D3D fallback paths. |
| `DxCdgD3D11TexturePresenter` | Active DXCDG indexed/palette D3D11 shader presenter. |

Retired bitmap CDG helpers such as `DirectCompositionCdgOverlayUploader`, `NativeCdgOverlayTextureUploader`, and `NativeCompositeFrameComposer` are preserved only under `LegacyCdg/` as disabled reference material.

---|---|
| `OptimizedPresentationRouter` | Chooses and sizes active D3DImage/native HWND/DirectComposition presentation paths. |
| `DirectCompositionCdgOverlayUploader` | Prepares and uploads CDG overlay pixels for the DirectComposition overlay path. |
| `NativeCdgOverlayTextureUploader` | Prepares source-sized GPU CDG overlay texture for native HWND retained overlay path. |
| `NativeCompositeFrameComposer` | Composes fallback/native BGRA frames, including CDG background/image layer rules. |
| `NativeCompositePresentationScheduler` | Coalesces static/native composite presentation requests and dirty-state timing. |
| `NativeCompositeBaseState` | Stores native composite base frame/state. |
| `CpuWriteableBitmapRenderer` | Owns WPF `WriteableBitmap` fallback rendering and CPU CDG conversion. |

`OptimizedGraphicsRenderer` should remain the place where session-level render choices are coordinated, but it should avoid owning large pixel conversion buffers or backend-specific upload internals.

---

## CDG/background layer rules

When CDG/overlay content is present:

| Layer | Rule |
|---|---|
| Background media/image | Draw to full viewport using `UniformToFill`. |
| CDG wash/background color | Full viewport rectangle/clear using CDG background color and transparency. |
| CDG lyrics | Draw native CDG frame as transparent overlay, aspect-fit into viewport. |

Video-only content should continue to use the normal video display mode/stretch path and must not inherit the CDG background-media `UniformToFill` rule.

---

## Maintenance guidelines

1. Keep `VorticeD3D11Renderer` as a facade. Do not add new backend-specific logic there unless it is only forwarding to presentation services.
2. Use `D3D11PresentationTargetSnapshot` for any frame path that needs the active presentation target.
3. Keep active CDG output in the DXCDG snapshot/presenter path. Do not reconnect the retired bitmap CDG overlay uploaders.
4. Do not tie background-media stretch to CDG frame dirty state. Base/background rules should come from session/layer state.
5. When changing resize logic, test native HWND resize aggressively because presentation targets can be recreated/adopted mid-frame.

## Experimental DXCDG direction

DXCDG is now the active CDG path. CDG packets update a framework-neutral indexed CDG model under `Core/DxCdg`, and the D3D11 DXCDG presenter renders that model directly as a GPU layer. The retired bitmap CDG code is preserved only under `LegacyCdg/` as disabled reference material.

See `FatzBombMedia_DXCDG_Roadmap.md`.

