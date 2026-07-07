<!-- Design note for stage 007A. Pure mapping + seam ruling. No behavior change. -->

# 007A — Present Flow Lock / Lifetime Map

Stage file: `docs/roadmap/007A_BACKLOG_PresentOutsideLocks.md`
Executes the stage's *First Safe Step*: "Produce a lock/lifetime map for
present flow before any code edit." This note is analysis only. No code was
changed in this sub-stage.

The map answers three questions:

1. Which locks wrap the GPU present, on which threads, protecting what state?
2. Which object lifetimes cross the present call boundary?
3. Is there a safe seam to move present work outside the transport-visible
   lock **without** pulling in `007B` (hardware frame release safety), `018A`
   (CDG snapshot ownership), or `019A` (`VideoFrameEventArgs` reuse redesign)?

## 1. Locks in the present path

There are three synchronization primitives in the coordinator + bridge, plus
one interlocked flag. Only two of them wrap the GPU present.

| Lock | File | Guards | Wraps GPU present? |
|---|---|---|---|
| `NativeMediaPresentationCoordinator._lock` | `FatzBombMedia.MediaControl/Core/Graphics/NativeMediaPresentationCoordinator.cs:43` | Composition background snapshot fields (`_compositionBackgroundBgra`, `_compositionBackgroundCopyBuffer`, `_compositionBackgroundWidth/Height/Stride/StretchMode`, `_compositionBackgroundIsRetainedGpu`, `_compositionBackgroundRevision`), surface dictionary (`_surfaces`), primary-surface attach/detach/resize, device-lost recovery, and calls into `_presenter.*`. | **Yes** — this is the transport-visible lock the stage targets. |
| `NativeMediaPresentationCoordinator._cdgPresentationGate` | `NativeMediaPresentationCoordinator.cs:44` | Pending-CDG flags (`_cdgPresentationPending`, `_cdgPendingConsumeDirtyRegions`, `_cdgPendingRetainedRefresh`, `_cdgPendingReason`) and `_lastCdgSnapshot`. | No — always released before the GPU present call. |
| `NativeD3D11PresentationBridge._lock` | `FatzBombMedia.MediaControl/Core/Graphics/D3D/NativeD3D11PresentationBridge.cs:14` | The D3D11 immediate context (`ID3D11DeviceContext`) and swap-chain / DirectComposition surface state exposed by `D3D11RendererPresentationServices`. Serializes GPU work against attach/resize/detach on that device. | **Yes** — but this one is *correct and necessary*. See §5. |
| `_recoveryInProgress` (Interlocked) | `NativeMediaPresentationCoordinator.cs:50, 387` | Debounces device-lost recovery so at most one recovery worker runs. | N/A. |

Lock nesting seen in the source: `_lock` → `_presenter.<method>` → `_lock`
(bridge). The `_cdgPresentationGate` is acquired outside `_lock` in every
call site; nesting the other direction was not observed.

## 2. Present entry points, threads, and lock ownership

Each row is a distinct call chain that reaches a swap-chain `Present()` or
DirectComposition `Commit()`. "Under `_lock`?" means the coordinator's
transport-visible lock is held across the GPU work.

| # | Entry site | Producing thread | Under `_lock`? | Terminal GPU call |
|---|---|---|---|---|
| 1 | `OnVideoFrameReady` → `PresentPrimaryVideoFrame` (BGRA path) → `PresentMediaComposition` (`NativeMediaPresentationCoordinator.cs:603, 621, 996`) | `VideoPipeline` presentation scheduler (background) | Yes | `MediaCompositor.PresentToTargets` → swap-chain `Present()` / DirectComposition `Commit()` |
| 2 | `OnVideoFrameReady` → `PresentPrimaryVideoFrame` (hardware path) (`NativeMediaPresentationCoordinator.cs:630-639`) | `VideoPipeline` presentation scheduler (background) | Yes | `_presenter.PresentHardwareFrame(e)` → video-processor blit + swap-chain `Present()` |
| 3 | `OnVideoFrameReady` (with CDG) → `PresentVideoFrameAsCdgBackground` → `PresentRetainedCdgFrame` / `RenderHardwareVideoCompositionFrame` (`NativeMediaPresentationCoordinator.cs:642-668, 821-868`) | `VideoPipeline` presentation scheduler (background) | Yes | Same as row 1 |
| 4 | `OnCoreBackgroundFrameReady` / `OnCoreImageFrameReady` (BGRA + hardware paths) (`NativeMediaPresentationCoordinator.cs:523-580`) | Core image/background frame producer (background) | Yes | Same as row 1 |
| 5 | `OnCoreBackgroundCleared` (`:582-601`) | Core background source (background) | Yes | Same as row 1 |
| 6 | `RenderLoop` (CDG cadence) → `PresentPendingCdgFrame(deferIfCompositionBusy: true)` → `RenderCurrentCdgFrame` (`:307-326, 709-734, 786-809`) | `_presentationScheduler` (dedicated worker) | **Try-lock** — `_lock.TryEnter()`, defers on contention | Same as row 1 |
| 7 | `RequestCdgPresentation` (idle path when `!_isRenderingActive`) → `PresentPendingCdgFrame(deferIfCompositionBusy: false)` (`:682-696`) | CDG engine event thread | Yes | Same as row 1 |
| 8 | `PresentCurrentComposition` (attach / resize / display-mode / recovery) (`:105-215, 240-244, 959-994`) | Host / UI or recovery worker | Called *outside* `_lock`, then re-enters `_lock` inside `PresentMediaComposition` | Same as row 1 |
| 9 | `ClearVisibleComposition` (`:1137-1152`) | Various | Yes | Same as row 1 |
| 10 | `RefreshCurrentPosition` (`:240-245`) | Host / UI | No (calls into row 8 semantics) | Same as row 1 |
| 11 | Attach / resize / detach primary surface (`:105-238`) | Host / UI | Yes — while calling `_presenter.CreateDirectCompositionSurface`, `CreateNativeHwndSurface`, `Queue*Resize`, `CleanupResources` | Swap-chain resize / DXGI adapter calls (not a swap `Present` but blocks on GPU work) |
| 12 | Device-lost recovery (`RunDeviceLostRecovery`) (`:398-454`) | `_recoveryScheduler` (dedicated worker) | Yes | `_presenter.CleanupResources` + `EnsureD3D11` + `RecreateAttachedSurfacesNoLock` |

Row 6 already uses the pattern the stage wants: `TryEnter` and defer rather
than block. Rows 1, 3, 4, 5 are the *primary* contention producers, because
they run per frame at video / background rate on a background thread and
each one blocks `_lock` for the duration of a vsync-limited `Present()`.

## 3. What holding `_lock` across the GPU present actually blocks

With `_lock` held for the vsync interval (~16.6 ms at 60 Hz — can be longer on
DirectComposition / secondary displays), the following requests wait:

- `AttachPresentationSurface`, `ResizePresentationSurface`,
  `SetPresentationSurfaceLiveResize`, `DetachPresentationSurface`
  (`NativeMediaPresentationCoordinator.cs:105-238`). These are UI-thread
  operations reached through the WPF adapter's HWND lifecycle
  (`FatzBombMedia.MediaControl.Wpf.MediaSurface`), so they stall the UI when
  a video frame is presenting.
- `PresentCurrentComposition` from display-mode change, refresh, attach
  callback (`:101, 129, 187, 212, 244`).
- Device-lost recovery entering its critical section (`:418`).
- `ClearVisibleComposition` (`:1143`) — used by attach refresh, stop,
  disposal.
- `RenderLoop`'s CDG present when `TryEnter` fails (`:794`) — presents drop
  to the "deferred" branch and are requeued. Under sustained contention this
  visibly under-drives CDG.
- Composition-state mutation calls that also take `_lock`:
  `SetCompositionLayer0Frame` (`:1078-1113`), `ClearCompositionLayer0Frame`
  (`:1115-1121`), `StoreRetainedHardwareBackgroundFrame` (`:870-888`),
  `BuildCompositionFrame` (`:1017-1025`).

This is the VLC-comparison finding, verified in-source.

## 4. Object lifetimes crossing the present call

For a seam to be safe, whatever the compositor / presenter reads *after*
`_lock` is released must be either (a) owned by the frame descriptor for the
duration of the present, or (b) protected by a still-held lock (the bridge's
`_lock`). The relevant lifetimes:

### 4.1 Software / BGRA background pixels — **aliased, NOT owned by the frame**

`SetCompositionLayer0Frame` at `NativeMediaPresentationCoordinator.cs:1078-1113`
writes into a **shared, reused** copy buffer and stores a `ReadOnlyMemory<byte>`
view over it:

```csharp
if (_compositionBackgroundCopyBuffer == null || _compositionBackgroundCopyBuffer.Length != required)
    _compositionBackgroundCopyBuffer = new byte[required];

frame.PixelData.Span[..required].CopyTo(_compositionBackgroundCopyBuffer);
_compositionBackgroundBgra = new ReadOnlyMemory<byte>(_compositionBackgroundCopyBuffer, 0, required);
```

`BuildCompositionFrameNoLock` (`:1041, 1063`) then sets
`MediaCompositionFrame.BackgroundBgra = _compositionBackgroundBgra`. The
`MediaCompositionFrame` is a value / `record`-style descriptor but the
underlying `byte[]` is the coordinator's shared buffer. If a second video
frame arrives before the first present finishes reading, `CopyTo` overwrites
the same buffer in place, and the in-flight present sees a torn image.

**Where the pixels are consumed:**

- `D3D11MediaCompositionPresenter.PresentToTargets`
  (`FatzBombMedia.MediaControl/Core/Graphics/Composition/D3D11MediaCompositionPresenter.cs:226-352`)
  calls `_layerCache.Capture(frame)` (line 271) and then
  `EnsureCompositionBackgroundFrame` (line 290), which reaches
  `backgroundLayer.UploadBgraPixels(..., bgraPixels, ...)`. The upload reads
  from the aliased span until the GPU staging copy completes (a memcpy into
  a mapped `ID3D11Texture2D`, not asynchronous — but performed **on the
  D3D11 immediate context** which is currently serialized only by the
  bridge's `_lock`, not the coordinator's).

Consequence: if `_lock` is released before `UploadBgraPixels` finishes
copying pixels off, the copy source can be mutated by a concurrent
`SetCompositionLayer0Frame` on another thread. This is the aliasing hazard.

### 4.2 Retained-GPU background — texture *pointer* not owned by anything

`StoreRetainedHardwareBackgroundFrame` and `RenderHardwareVideoCompositionFrame`
(`NativeMediaPresentationCoordinator.cs:821-888`) store raw
`frame.D3D11TexturePointer` values (`VideoFrameEventArgs.D3D11TexturePointer`)
and pass them through `MediaCompositionFrame.BackgroundD3D11TexturePointer`.
The lifetime of that texture belongs to the video decoder's frame pool
(`FatzBombMedia.MediaControl.Pipeline.VideoPipeline` / hardware decoder). It
is **not** ref-counted by `MediaCompositionFrame`. Under today's design, the
decoder is prevented from recycling that texture only because the coordinator
holds `_lock` for the whole present, which pins the frame indirectly via
back-pressure in the video pipeline (frame producer is on the same thread as
`OnVideoFrameReady` for hardware frames — verified: rows 2/3 above run on
`VideoPipeline` scheduler).

Consequence: releasing `_lock` before the GPU has consumed the texture is
**not** safe for the hardware path without an explicit AddRef / release
protocol. That is precisely the work reserved for stage `007B`. This is a
hard boundary for `007A`.

### 4.3 CDG snapshot — externally owned but currently immutable

`CdgRenderSnapshot` handed off through `MediaCompositionFrame.CdgOverlay` is
produced by `ICdgSnapshotSource.TryCreateCdgSnapshot`
(`NativeMediaPresentationCoordinator.cs:944-957`) and cached in
`_lastCdgSnapshot`. Whether the snapshot's `ColorIndices` / `PaletteArgb`
arrays are copies or aliased views into the CDG engine's state is the
subject of stage `018A` (CDG Snapshot Ownership Redesign). For today's
purposes: the snapshot is only *replaced* under `_cdgPresentationGate`, never
mutated in place — so a snapshot handed to a present can be safely read for
the duration of that present even if `_lock` is released, provided the
present holds the reference (which `MediaCompositionFrame.CdgOverlay` does).

Consequence: the CDG snapshot lifetime is *not* the blocker for `007A`.

### 4.4 Composition frame descriptor itself

`MediaCompositionFrame` is a `record`-style descriptor; the value passed to
`PresentMediaComposition` is captured by the caller and does not alias
mutable coordinator state *except* through `BackgroundBgra` and the D3D11
texture pointer. Same conclusion: descriptor is safe; its inner references
are the problem.

### 4.5 Swap chains, DirectComposition surfaces, `_surfaces` dictionary

These are guarded by `_lock` today. If `_lock` were released across the
present, a concurrent attach / detach / resize could tear down the very
target the compositor is drawing to. The bridge's `_lock` already covers
the compositor's device-context use, but does **not** protect
`_surfaces` (the dictionary lives on the coordinator). See §5.2.

## 5. Seam analysis

### 5.1 The bridge lock is not the target

`NativeD3D11PresentationBridge._lock` serializes the D3D11 immediate context
and the DXGI / DirectComposition surface calls. The immediate context is not
thread-safe, and swap-chain / DirectComposition APIs must not be interleaved
with device teardown. Removing this lock would introduce well-known
D3D11 races. The stage's goal is met by keeping this lock and demoting the
*coordinator* lock — not by removing bridge synchronization.

### 5.2 Coordinator lock — three roles, only one is expensive

The coordinator's `_lock` currently plays three roles:

1. **State mutation lock** — protects the composition-background snapshot
   fields (`_compositionBackgroundBgra`, revision, retained-GPU flags, etc.)
   and the `_surfaces` dictionary.
2. **Snapshot build lock** — held across `BuildCompositionFrameNoLock`.
3. **Present lock** — held across the call into
   `_presenter.PresentMediaCompositionVisible` /
   `_presenter.PresentHardwareFrame` (which each acquire the bridge lock
   internally).

Roles 1 and 2 are cheap and must remain locked. Role 3 is the vsync-bound
one and is the target. The seam is: **build a genuinely owned
`MediaCompositionFrame` under `_lock`, then release `_lock` before calling
`_presenter.*`.**

### 5.3 Preconditions for the software / BGRA path

To make the software-path present safe outside `_lock`, the following must
hold (all three, at once, for the software path only):

1. **Owned pixel snapshot.** `MediaCompositionFrame.BackgroundBgra` must
   reference a `byte[]` (or pooled buffer) whose ownership passes to the
   frame for the duration of the present, so a concurrent
   `SetCompositionLayer0Frame` cannot overwrite it. Two viable
   implementations, both local to the coordinator:
   - *Buffer swap:* replace the single reusable
     `_compositionBackgroundCopyBuffer` with a "current" + "next" pair,
     where the frame captures the "current" reference and any new incoming
     pixels go into "next". Rotate on the next mutation.
   - *Small pool:* an `ArrayPool<byte>`-backed pool with the frame carrying
     a rented buffer that is returned to the pool by the caller after the
     present completes.
   Either preserves today's allocation profile.
2. **CDG snapshot reference is captured into the frame before releasing
   `_lock`.** Already true — `BuildCompositionFrameNoLock` reads
   `_lastCdgSnapshot` under lock via `TryGetCdgSnapshotForVideoComposition`
   / `PresentRetainedCdgFrame`. No change needed here.
3. **Surface-set changes cannot tear down the target mid-present.** The
   bridge's `_lock` serializes actual swap-chain access, so the swap chain
   itself is safe. The remaining hazard is `DetachPresentationSurface`
   removing an entry from `_surfaces` while the compositor is iterating the
   snapshot returned by `Surfaces.GetActiveTargetSnapshots`. Verified: the
   compositor receives an `IReadOnlyList<D3D11PresentationTargetSnapshot>`
   (a value snapshot) from `D3D11PresentationSurfaceController` and does
   not re-enter the coordinator's `_surfaces` during draw
   (`D3D11RendererPresentationServices.cs:266-291`). So the target list is
   already an owned snapshot. The remaining risk is only the underlying
   `Texture` handle inside a snapshot going away; that is bridge-lock
   territory and is already covered.

If (1) is done, (2) and (3) are already true today. That is the important
finding: **the software-path seam does not require touching `018A` or the
target snapshot infrastructure.** It requires a bounded, coordinator-local
change to how the composition background buffer is owned.

### 5.4 Hardware / retained-GPU path — deferred to `007B`

Every hardware entry site (rows 2 and 3 above, plus
`StoreRetainedHardwareBackgroundFrame`) passes a raw
`D3D11TexturePointer` whose lifetime is owned by the video-pipeline decoder
pool. There is no in-repo AddRef / release protocol on
`VideoFrameEventArgs.D3D11TexturePointer`. Releasing `_lock` before the GPU
has finished consuming the frame would introduce use-after-free / recycle
races on that pointer.

Ruling: hardware-path present **stays under `_lock`** in `007A`. It is
deferred to `007B` (Hardware Frame Release Safety), which is already the
next backlog item after `007A`.

### 5.5 Device-lost recovery, attach / resize / detach

These are not the per-frame contention producers and already run on their
own scheduler (`_recoveryScheduler`) or the UI thread. They must remain
under `_lock` because they mutate `_surfaces` and rebuild devices. The win
is that when the software-path present no longer holds `_lock` across
vsync, these operations will *acquire* `_lock` much faster — that is the
actual UX improvement for pause / stop / seek / resize.

## 6. Ruling

- **A safe software-path seam exists in `007A` scope.** It requires exactly
  one bounded change: converting the composition background pixel buffer
  from a single reused array + aliased `ReadOnlyMemory<byte>` view into an
  owned snapshot per built `MediaCompositionFrame` (§5.3, item 1).
- **Bridge lock stays.** It is the correct D3D11 device-context lock.
- **Hardware / retained-GPU path stays under `_lock`.** Moving it requires
  the frame-release protocol that is stage `007B`.
- **No `018A` / `019A` / `017A` work is required.** The CDG snapshot and
  target snapshot lifetimes are already frame-owned.

## 7. Session 2 scope (recommendation for `/implement 007A.2`)

Session 2 should be small and reversible:

1. Introduce an owned pixel snapshot (buffer-pair or pooled) in the
   coordinator; update `SetCompositionLayer0Frame` and
   `BuildCompositionFrameNoLock` so the frame carries an owned buffer.
2. In `PresentMediaComposition` (`NativeMediaPresentationCoordinator.cs:996`),
   split into two steps: (a) `BuildCompositionFrame` under `_lock`, (b) call
   `_presenter.PresentMediaCompositionVisible(frame)` **outside** `_lock`.
   The bridge continues to lock internally.
3. Do **not** change `PresentPrimaryVideoFrame`'s hardware branch, nor
   `RenderHardwareVideoCompositionFrame`, nor
   `StoreRetainedHardwareBackgroundFrame`. Those keep the outer `_lock` for
   `007B`.
4. Leave `ClearVisibleComposition`, `PresentCurrentComposition`, attach /
   resize / detach paths unchanged. They already tolerate the lock; the win
   is that they *acquire* it faster.
5. `RenderLoop`'s CDG `TryEnter` / defer pattern (`:794`) stays. If both
   the software present is out of the lock and CDG can `TryEnter` faster,
   deferred CDG presents should approach zero under normal load — that is
   the metric to watch during smoke, not something to change.

## 8. Validation expected for Session 2

Same matrix as the stage's *Validation* section, unchanged:

- `dotnet build FatzBombMedia.sln -c Release` — clean, warnings-as-errors.
- Full manual smoke per `docs/VALIDATION.md`: play / pause / stop / seek,
  CDG, video, sidecar video, secondary output, resize / fullscreen.
- Specific regression watches after the seam edit:
  - Tearing on the primary video BGRA layer (would indicate the owned-buffer
    swap is not actually swapping — the aliasing hazard slipped through).
  - CDG-only playback (verifies `RenderLoop` `TryEnter` still integrates).
  - Hardware video (verifies rows 2/3 were left alone).
  - Rapid pause / stop / seek during playback (the target UX win — should
    feel noticeably snappier).
  - Live resize (`SetPresentationSurfaceLiveResize`) — must remain
    stall-free.

## 9. Stop conditions for Session 2

- Any touch to `VideoFrameEventArgs.D3D11TexturePointer` lifetime →
  escalate to `007B`, do not continue.
- Any touch to `CdgRenderSnapshot` internals → escalate to `018A`.
- Any touch to `D3D11PresentationSurfaceController` snapshot semantics →
  out of scope; the current snapshot behavior is already sufficient.
- If the owned-buffer change requires more than the coordinator file plus
  the composition frame descriptor, stop and re-scope.
