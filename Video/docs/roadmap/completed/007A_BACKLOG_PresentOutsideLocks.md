<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: COMPLETE.

# Stage 007A — Present Outside Transport-Visible Locks


## Status

Complete. Sub-stage 007A.1 (lock/lifetime map + seam ruling) and
sub-stage 007A.2 (software-path seam) both done. Manual smoke passed.

## Goal

Reduce lock contention by ensuring expensive GPU present work is not performed while holding transport-visible/pipeline locks.

## Why This Matters

The VLC comparison identified `Present` under multiple locks as a major contention flaw. With vsync, this can stall pause/stop/seek and CDG coordination.

## Intended Direction

- Map current lock ownership around `FrameReady`, coordinator handling, D3D blit, and swap-chain present.
- Move expensive present work outside inappropriate locks if a safe seam exists.
- Preserve frame lifetime and presentation correctness.

## Not Allowed

- No display-rate compositor rewrite.
- No GPU fence/release redesign in this stage unless explicitly promoted.
- No broad `VideoPipeline` split.

## Sub-stages

### 007A.1 — Lock / lifetime map + seam ruling (COMPLETE)

Design note: `docs/roadmap/007A_NOTE_LockLifetimeMap.md`.

Findings (see the note for the full map):

- Two locks wrap the GPU present: the coordinator's transport-visible
  `_lock` and the D3D11 bridge's `_lock`. The bridge lock is correct and
  necessary (it serializes the D3D11 immediate context) and must stay.
- The coordinator's `_lock` plays three roles: state mutation, snapshot
  build, and — problematically — GPU present. Only the third is expensive.
- The software / BGRA background pixel buffer is aliased shared memory
  (`_compositionBackgroundCopyBuffer` reused across frames, exposed as a
  `ReadOnlyMemory<byte>` view). Releasing `_lock` around the present without
  fixing this creates a torn-read hazard on the compositor's upload path.
- Hardware / retained-GPU frames pass raw `D3D11TexturePointer` values
  whose lifetime is owned by the video decoder pool. Moving the hardware
  path outside `_lock` requires the frame-release protocol reserved for
  stage `007B` — hard boundary for `007A`.
- Ruling: **A safe software-path seam exists in `007A` scope.** It requires
  one bounded coordinator-local change (owned pixel snapshot per built
  `MediaCompositionFrame`). CDG snapshot and target snapshot lifetimes are
  already frame-owned; `018A` / `019A` / `017A` work is not required.

### 007A.2 — Software-path seam (COMPLETE)

Implemented in `FatzBombMedia.MediaControl/Core/Graphics/NativeMediaPresentationCoordinator.cs`.

What changed:

1. Owned pixel snapshot per built `MediaCompositionFrame` via a dedicated
   `CompositionBackgroundPool` (`ArrayPool<byte>` sized for up to ~4K BGRA
   frames). `BuildCompositionFrameNoLock` copies the background into a
   rented buffer and returns it through an `out` parameter; the buffer is
   returned to the pool after the present completes.
2. `SetCompositionLayer0Frame` no longer takes the
   `PixelDataCanBeRetained` fast-path — the master
   `_compositionBackgroundCopyBuffer` is now only ever touched under
   `_lock`, closing the torn-read hazard.
3. New `BuildAndPresentSoftwareComposition` (build under `_lock`) +
   `PresentSoftwareComposition` (present outside `_lock`). The three
   software call sites — core image, core background, primary BGRA video —
   route through it.
4. `RenderCurrentCdgFrame` split: `TryBuildCdgCompositionFrameNoLock`
   builds the snapshot + frame under `_lock`; the GPU present runs outside
   it. The existing `TryEnter` / defer cadence is preserved.
5. Hardware / retained-GPU paths deliberately left under `_lock` —
   deferred to stage `007B` (raw decoder texture pointers, no ownership
   protocol). `ClearVisibleComposition` and empty-frame presents unchanged.

Robustness: the rented-buffer copy in `BuildCompositionFrameNoLock` is
try-guarded so the buffer is returned to the pool if the copy throws
(effectively infallible for same-length aligned copies, but belt-and-
suspenders against pool leaks).

Validation:

- `dotnet build FatzBombMedia.sln -c Release` — 0 warnings, 0 errors
  (warnings-as-errors on Core).
- `dotnet test` — 162 passed, 0 failed.
- `scripts/check-wpf-boundary.ps1` — passed.
- `scripts/check-presentation-parity.ps1` — passed.
- Manual smoke (per `docs/VALIDATION.md` + note §8 regression watches):
  play/pause/stop/seek, CDG, video, sidecar video, secondary output,
  resize/fullscreen — **passed**. No BGRA tearing, no CDG cadence
  regression, hardware video unchanged, rapid pause/stop/seek snappier.

Stop conditions (from the note, §9): none triggered — no hardware frame
lifetime, `CdgRenderSnapshot` internals, or
`D3D11PresentationSurfaceController` snapshot semantics were touched.

## First Safe Step

Produce a lock/lifetime map for present flow before any code edit.
**Done** — see `007A_NOTE_LockLifetimeMap.md`.

## Validation

High-risk timing/threading/presentation stage. Use full builds/scripts and targeted smoke: play/pause/stop/seek, CDG, video, sidecar video, secondary output, resize/fullscreen.

Post-`007A.2` regression watches (from the note, §8): primary video BGRA
tearing (aliasing hazard indicator), CDG-only playback, hardware video,
rapid pause/stop/seek during playback, and live resize.
