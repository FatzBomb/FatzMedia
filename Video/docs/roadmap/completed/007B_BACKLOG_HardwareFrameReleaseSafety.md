<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: COMPLETE.

# Stage 007B — Hardware Frame Release Safety


## Status

Complete. Sub-stage 007B.1 (hardware frame ownership audit + release-safety
ruling) and sub-stage 007B.2 (documentation + debug assertion) both done.

## Goal

Review and improve D3D11 hardware frame release safety so FFmpeg/D3D surfaces are not recycled before GPU work is safely complete.

## Why This Matters

Hardware frames are zero-copy and efficient, but release timing without a GPU completion boundary can create driver-dependent visual artifacts.

## Intended Direction

- Map FFmpeg hardware-frame ownership and D3D11 present/blit lifetime.
- Decide whether a fence, deferred release, or retained reference boundary is needed.
- Preserve zero-copy hardware path where possible.

## Not Allowed

- No decoder rewrite.
- No shared demuxer work.
- No broad renderer architecture change.

## First Safe Step

Audit hardware frame ownership from decoder queue through composition/present release.
**Done** — see `007B_NOTE_HardwareFrameOwnershipMap.md`.

## Sub-stages

### 007B.1 — Hardware frame ownership audit + release-safety ruling (COMPLETE)

Design note: `docs/roadmap/007B_NOTE_HardwareFrameOwnershipMap.md`.

Findings (see the note for the full map):

- The coordinator and decoder share the **same D3D11 device and immediate
  context** (established by `EnsureD3D11ForHardwareFrame` →
  `InitializeFromNativeDevice`, which AddRef's the decoder's context).
  All GPU work — decode, video-processor blit, swap-chain Present — runs
  on one sequential immediate context.
- This shared-context serialization is the **actual safety mechanism**: the
  decoder cannot reuse a texture until the blit has finished, because both
  are on the same sequential command stream.
- Additional layers: AddRef on the source texture during the blit call,
  `Context.Flush()` after the blit, one-frame retention in `PresentFrame`,
  and D3D11 internal resource tracking.
- Ruling: **No fence, deferred-release ring, or retained-reference boundary
  is required for correctness in the current architecture.** The safety is
  implicit in the shared-device invariant.
- Risk: the invariant is undocumented. A future change that breaks it
  (e.g., `017A` display-rate compositor using a deferred context) would
  require a fence or deferred-release ring.

### 007B.2 — Documentation + debug assertion (COMPLETE)

Implemented in:
- `FatzBombMedia.MediaControl/Core/Graphics/NativeMediaPresentationCoordinator.cs`
  — documented the shared-device invariant on `EnsureD3D11ForHardwareFrame`
  and added two `Debug.Assert` checks that the coordinator's device pointer
  matches the frame's device pointer (after the fast-path match and after
  `InitializeFromNativeDevice`).
- `FatzBombMedia.MediaControl/Core/Graphics/D3D/Device/D3D11DeviceResources.cs`
  — documented the shared-context safety invariant on
  `InitializeFromNativeDevice`.

No fence, no deferred-release ring, no API changes — the current design is
correct; the work was making the implicit safety invariant explicit.

Validation:
- `dotnet build FatzBombMedia.sln -c Release` — 0 warnings, 0 errors.
- `dotnet test` — 162 passed, 0 failed.
- Manual smoke (hardware video playback, seek, stop, media switch,
  secondary output, device-lost recovery) recommended but not run in this
  session — no behavior changed, only comments + debug assertions.

Stop conditions (from the note, §8): none triggered — no
`VideoFrameEventArgs` public shape change, no decoder/hwframe pool change.

## Validation

High-risk decoder/presentation lifetime stage. Use full builds/scripts and smoke hardware video playback, seek, stop, media switch, secondary output.
