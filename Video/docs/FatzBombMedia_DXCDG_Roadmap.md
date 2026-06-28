# FatzBombMedia DXCDG Roadmap

## Current status

DXCDG is now the active CDG rendering direction. The old bitmap-first CDG engine and old bitmap/overlay presenter helpers are preserved under `FatzBombMedia.MediaControl/LegacyCdg/` as `.disabled` reference files only.

No `AppContext` switches are required for normal CDG playback. The active path is:

```text
CDG packets
  -> DxCdgEngine
  -> DxCdgStateMachine
  -> DxCdgRenderSnapshot
  -> DxCdgD3D11TexturePresenter
  -> active D3D11 presentation backend
```

## Why DXCDG replaced the bitmap path

The bitmap path kept CDG tied to full-frame ARGB generation, BGRA conversion buffers, scaled overlay buffers, DirectComposition bitmap upload, native cached overlay upload, and CPU blend/composite fallback paths. That made CDG the main remaining rendering bottleneck and made it unclear whether new renderer work was actually being used.

DXCDG keeps the CDG frame as indexed color data plus palette/wash state, then resolves pixels in a D3D11 shader.

## Active implementation files

| File | Purpose |
|---|---|
| `Core/DxCdg/DxCdgEngine.cs` | Active `ICdgEngine` implementation. |
| `Core/DxCdg/DxCdgStateMachine.cs` | Authoritative indexed CDG packet/state model. |
| `Core/DxCdg/DxCdgRenderSnapshot.cs` | Snapshot handed from engine to presenter. |
| `Core/DxCdg/DxCdgD3D11TexturePresenter.cs` | D3D11 texture/shader presenter for visible CDG output. |
| `Core/DxCdg/DxCdgColorManager.cs` | Palette/background/wash color state. |
| `Core/DxCdg/DxCdgPacket.cs` | Parsed 24-byte CDG packet wrapper. |

## Current debugging focus

The active DXCDG visible path now logs each handoff with `[DXCDG-TRACE]` and `[DXCDG-D3D11]` messages. If CDG is not visible, check the log sequence:

```text
[DXCDG-TRACE] MediaRenderer snapshot ...
[DXCDG-TRACE] OptimizedRenderer present request ...
[DXCDG-TRACE] Present active target snapshot ...
[DXCDG-D3D11] Drawing active target ...
[DXCDG-D3D11] Presented active target ...
[DXCDG-TRACE] Presenter draw succeeded; notifying backend ...
[DXCDG-TRACE] Backend present ... / D3DImage AddDirtyRect ...
```

The first missing line indicates where presentation stopped.

## Remaining DXCDG work

- Verify visible presentation on D3DImage, native HWND, and DirectComposition backends.
- Finish full-window CDG background/wash behavior.
- Validate CDG-only, CDG+audio, CDG+background image, and CDG+background video.
- Remove or archive disabled legacy files after DXCDG is stable enough that they are no longer useful as reference.
