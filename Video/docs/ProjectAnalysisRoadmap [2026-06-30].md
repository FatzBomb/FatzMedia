# Project Analysis Roadmap - FatzBombMedia.MediaControl

Last updated: 2026-07-02

This is the active roadmap. Historical diagnostics, completed stage details, and raw profiling plans
have been removed. Use `docs/VALIDATION.md` for the full manual smoke checklist.

## Current State

- `FatzBombMedia.MediaControl` remains the UI-agnostic Core engine. The WPF adapter boundary is
  intentionally strict: WPF owns HWND/control interop only and must not reference Core renderer,
  pipeline, internal CDG, video-frame, or D3D11 implementation details.
- The VT-F timing/memory work is complete. The production fixes that should remain are the
  nonblocking audio-position snapshot, timeline-ahead audio-push backpressure, and calibrated
  source-cadence pacing hot-path cleanup.
- Investigation-only VT-F diagnostics were removed from `VideoPipeline.cs`. Keep the lightweight
  `[VideoPipeline:Loop]` and `[VideoPipeline:Perf]` summaries unless a future task explicitly
  replaces them with structured logging.
- Long-run memory behavior for the canonical 23.98fps file is now stable. Any remaining rare jitter
  should be treated as a display/presentation-cadence investigation, not another blind selector,
  decoder, or audio-clock change.
- No dedicated test project or CI exists. The current practical safety net is Debug build, Release
  build, WPF-boundary script, presentation-parity script, and manual `FatzBombMedia.Player` smoke
  testing.

## Validation Commands

Run these after source changes that affect Core, WPF interop, rendering, timing, audio, video, or
public behavior:

```powershell
dotnet build FatzBombMedia.sln --no-restore
dotnet build -c Release FatzBombMedia.sln --no-restore
pwsh -ExecutionPolicy Bypass -File ".\scripts\check-wpf-boundary.ps1"
pwsh -ExecutionPolicy Bypass -File ".\scripts\check-presentation-parity.ps1"
```

Manual media validation still matters for native/GPU paths. Use `docs/VALIDATION.md` and the Player
host, especially for audio+CDG, video with audio, video without audio, seek/scrub, secondary output,
fullscreen/resize, and long 23.98fps playback.

Canonical VT-F media:

- `D:\- Testing Files\23.98-stutter.mkv`
- `D:\- Testing Files\23.98-stutter-no-audio.mkv`

## Current Known Follow-Ups

No broad refactor should start until these are explicitly accepted or deferred.

| ID | Status | Issue | Likely owner | Next action |
|---|---|---|---|---|
| BUG-BGVIDEO-1 | Deferred research | One known AVI sidecar (`karaoke-inner-images.avi`) can appear to loop around ~1s when used with `karaoke-inner-images.zip`; other tested sidecar/background videos loop correctly and remain the priority path. | `PipelineMediaEngine.BackgroundVideoLoop`, `VideoDecoder`, background presentation timing | Keep `[BackgroundVideo]` diagnostics; do not change decoder/sidecar behavior until a fresh repro log proves the restart or PTS failure mode. |
| LOOP-POLISH-1 | Future polish | Primary/standalone video looping is functional and preserves the surface, but some files show a small hold/frame jump at the loop boundary. | `VideoPipeline`, `VideoAudioEngine`, future loop policy helper | Leave as acceptable for now; revisit only if seamless primary video looping becomes a hard requirement. |

## Recently Fixed Behavior Items

These fixes are implemented in source, have passed the standard build/boundary/parity checks, and passed
the focused manual Stage 2/3 smoke validation on 2026-07-03. Re-test native/GPU/media combinations after
future renderer, decoder, timing, or presentation changes.

| ID | Fixed behavior | Main files |
|---|---|---|
| BUG-TEMPO-1 | Video without audio now routes tempo through `ClockAudioEngine` / `PlaybackClock`. | `MediaEffects`, `ClockAudioEngine`, `PlaybackClock` |
| BUG-TEMPO-2 | Standalone CDG without audio now uses the same clock-backed tempo path. | `MediaEffects`, `ClockAudioEngine`, `PlaybackClock`, `CdgPipeline` |
| BUG-IMAGE-1 | Standalone image and stopped standalone visual presentation now show through the native surface without WPF adapter renderer knowledge. | `PipelineMediaEngine`, `NativeMediaPresentationCoordinator`, `MediaSurface` |
| BUG-SIDECAR-1 | Archive and same-base external sidecar/background resolution now builds a flat random candidate pool; explicit background arrays are supported on the concrete player API. | `MediaFile`, `MediaBackgroundResolver`, `MediaControlPlayer` |
| BUG-CDG-BG-1 | Sidecar/background frames now wait for a visible CDG snapshot instead of using reset-only CDG revisions as readiness. | `NativeMediaPresentationCoordinator`, `CdgStateMachine`, `D3D11MediaCompositionPresenter` |
| RELIABILITY-CDG-1 | Standalone CDG completion monitor now disposes its `CancellationTokenSource` after monitor exit. | `CdgPipeline` |
| RELIABILITY-ASYNC-1 | Core async paths now consistently use `.ConfigureAwait(false)`. | `MediaFile`, `AudioEngine`, `AudioNormalizer`, `CdgPipeline` |
| RELIABILITY-SCAN-1 | Directory scanner access failures now produce low-noise `Trace` diagnostics while preserving skip-and-continue behavior. | `DirectoryScanner` |

## Roadmap Principles

- Make one focused change per stage and build after each meaningful stage.
- Keep measurement, performance fixes, behavior changes, naming cleanup, and architecture refactors
  separate.
- Do not change frame-selection, decoder selection, audio processing, BASS clock semantics,
  normalization, tempo/pitch, WPF adapter boundaries, or renderer backend policy unless that is the
  explicit stage goal.
- Treat renderer, timing, native lifetime, and state ownership changes as high-care work.
- Prefer small behavior-preserving seams over broad rewrites.
- Use Graphify/source mapping before architecture, renderer, pipeline, or dependency-flow changes.

## Recommended Next Step

The focused Stage 2/3 manual validation pass is complete. Start **Stage 3A — MediaControlOperationQueue
async-worker mapping** next.

Stage 3A first pass is read-only/planning: map current operation-queue callers, ordering guarantees,
blocking points, cancellation/error behavior, and UI-thread risks before editing source. Do not start
device-loss recovery, primary-loop polish, BUG-BGVIDEO-1 AVI research, renderer architecture cleanup, or
structured logging until Stage 3A is mapped.

## Active Roadmap

### 1. Hot-Path Allocation Cleanup

Goal: reduce avoidable steady-state allocations without changing rendering output.

Useful context retained from the full analysis:

- `CdgEngine.RefreshBackgroundWashUnsafe` was identified as the smallest safe allocation win because it
  creates a full CDG snapshot only to read `BackgroundWashArgb`.
- Composition allocation cleanup is larger than the wash-color fix. It touches layer ordering,
  retained layer cache state, and dirty-region behavior, so keep it separate.
- CDG snapshot pooling is intentionally last. Pooling can reduce allocations, but buffer ownership and
  upload lifetime mistakes can cause stale lyrics, palette corruption, or use-after-return bugs.
- Seek-frame inspection and small D3D allocation fixes are lower risk than compositor pooling and can
  be handled as small isolated changes.

Current status:

- Done: CDG wash-color direct read, seek-frame direct queue enumeration, `VideoProcessorStream[]` reuse,
  composition target/layer scratch-list cleanup, composition layer accessors, update-plan LINQ removal,
  and `Marshal.SizeOf<CdgShaderConstants>()` caching.
- Deferred: CDG snapshot pooling. Do not use `ArrayPool` for current immutable CDG snapshots without a
  new ownership model; dirty-only snapshots or double-buffering would be a future architecture task.
- Deferred: `VideoFrameEventArgs` reuse. Event handlers may retain frame args or frame payloads, so reuse
  needs a separate event-lifetime review.

Primary files:

- `FatzBombMedia.MediaControl/Core/Cdg/CdgEngine.cs`
- `FatzBombMedia.MediaControl/Core/Cdg/CdgStateMachine.cs`
- `FatzBombMedia.MediaControl/Core/Video/VideoDecoder.cs`
- `FatzBombMedia.MediaControl/Pipeline/VideoPipeline.cs`
- `FatzBombMedia.MediaControl/Core/Graphics/Composition/D3D11MediaCompositionPresenter.cs`
- `FatzBombMedia.MediaControl/Core/Graphics/Composition/D3D11/D3D11CompositionLayerCache.cs`
- `FatzBombMedia.MediaControl/Core/Graphics/D3D/Video/D3D11VideoProcessorPresenter.cs`

Validation focus: audio+CDG, CDG+image/video background, video with audio, video without audio,
seek/scrub, secondary output, and allocation comparison for the touched path.

Stage guard rails:

- Do not change visual output, frame selection, timing, decoder selection, backend selection, or public
  API while doing allocation cleanup.
- Stop if a proposed optimization needs new cross-thread ownership rules; split that into its own
  reviewed stage.
- Treat a static allocation reduction as incomplete until a focused runtime check shows the intended
  path improved without visual regression.

**Latest review findings — 2026-07-02:**

- **Readiness:** Proceed with exclusions. Stage 1 contains multiple candidate items with varying risk.
  This review narrows auto-execution to the single smallest safe win: the CDG wash-color allocation fix.
  All other candidate items (composition layer churn, CDG snapshot pooling, seek-frame inspection,
  per-frame D3D allocations) are excluded from this auto-execute pass and require separate review.
- **Safety/Risk:** Low for the CDG wash-color fix. It is a one-method change: `CdgEngine.RefreshBackgroundWashUnsafe`
  currently calls `_cdgState.CreateSnapshot(consumeDirtyRegions: false)` (which allocates a full ~64 KB
  color-index array + palette array + dirty-rect array) just to read `snapshot.BackgroundWashArgb`. The
  fix reads `_cdgState.BackgroundWashArgb` directly, which returns the same `_backgroundWashArgb` field
  under the same `_cdgState._lock`. No state is mutated by `CreateSnapshot(false)`, so removing the call
  has no side-effect change. Lock ordering is unchanged (`_renderLock` then `_cdgState._lock`), and the
  constructor already uses the same `_cdgState.BackgroundWashArgb` read pattern.
- **Excluded from auto-execute:**
  - Composition layer churn (formerly Stage 1C): touches layer ordering, retained layer cache, and
    dirty-region behavior. Needs separate narrowed review.
  - CDG snapshot pooling (formerly Stage 1D): never auto-execute per command rules. Buffer lifetime/
    aliasing risk.
  - Seek-frame inspection and per-frame D3D allocation fixes: safe but separate changes. Review
    independently.
- **Auto-execute allowed:** Yes, for the CDG wash-color fix only.
- **Recommendation:** Proceed with exclusions.

**Execution — 2026-07-02 (CDG wash-color fix):**

- **Change:** `CdgEngine.RefreshBackgroundWashUnsafe` now reads `_cdgState.BackgroundWashArgb` directly
  instead of calling `_cdgState.CreateSnapshot(consumeDirtyRegions: false)`. The snapshot allocated a
  full ~64 KB color-index array + palette array + dirty-rect array on every call just to read one
  `uint`. `CreateSnapshot(false)` does not mutate state, so removing it has no side-effect change. Lock
  ordering is unchanged (`_renderLock` then `_cdgState._lock`); the constructor already uses the same
  direct-read pattern.
- **Files changed:** `FatzBombMedia.MediaControl/Core/Cdg/CdgEngine.cs` (one method).
- **Behavior changed:** No. Same wash-color value, same comparison, same return.
- **Debug build:** Pass (0 warnings, 0 errors).
- **Release build:** Pass (0 warnings, 0 errors).
- **WPF boundary check:** Pass.
- **Presentation parity check:** Pass.
- **Stale `DxCdg` search:** Clean (0 results).
- **Runtime/manual validation:** Not yet completed. Needed: audio+CDG with wash color changes, lyric
  updates, opacity/override changes, seek/scrub, standalone CDG, and secondary output CDG parity.
  Confirm background wash color is correct after lyric changes and seek.
- **Plan updated:** Yes.

**Latest review findings — 2026-07-02 (seek-frame inspection):**

- **Readiness:** Proceed with exclusions. The CDG wash-color fix is done. This review targets the next
  smallest safe item: seek-frame inspection. `VideoDecoder.GetQueuedFramesSnapshot()` returns
  `[.. _frameQueue]` (materializes the entire `ConcurrentQueue<VideoFrameData>` into an array) and is
  called from `VideoPipeline.HasAcceptableSeekFrame` inside the `DecodeForSeekTarget` loop, which runs
  up to 32-72 iterations during seek/scrub. Each iteration allocates a fresh array.
- **Safety/Risk:** Low. `VideoDecoder` is `internal sealed` — `GetQueuedFramesSnapshot()` is not public
  NuGet API. It has exactly one call site (`VideoPipeline.cs:1348`). The caller only iterates the
  result with `foreach` and reads `frame.PresentationTime`; it does not index, use `.Length`, or mutate.
  `ConcurrentQueue<T>` enumeration is already a snapshot (same semantics as `[.. _frameQueue]`), so no
  behavior change. The fix adds a new `EnumerateQueuedFrames()` method returning `_frameQueue` as
  `IEnumerable<VideoFrameData>` and changes the one call site. `GetQueuedFramesSnapshot()` is left
  unchanged (no opportunistic cleanup).
- **Excluded from auto-execute:** Composition layer churn, CDG snapshot pooling, per-frame D3D
  allocations — same as prior review. These need separate narrowed review.
- **Auto-execute allowed:** Yes, for the seek-frame inspection fix only.
- **Recommendation:** Proceed with exclusions.

**Execution — 2026-07-02 (seek-frame inspection):**

- **Change:** Added `VideoDecoder.EnumerateQueuedFrames()` returning `_frameQueue` as
  `IEnumerable<VideoFrameData>` (direct `ConcurrentQueue` enumeration, no array allocation).
  Changed `VideoPipeline.HasAcceptableSeekFrame` to use `EnumerateQueuedFrames()` instead of
  `GetQueuedFramesSnapshot()`. The old method materialized a fresh `VideoFrameData[]` on every
  iteration of the seek-decode loop (up to 32-72 iterations per seek). `ConcurrentQueue<T>`
  enumeration is already a snapshot, so semantics are unchanged. `GetQueuedFramesSnapshot()` left
  in place (no opportunistic cleanup).
- **Files changed:** `FatzBombMedia.MediaControl/Core/Video/VideoDecoder.cs` (one new method),
  `FatzBombMedia.MediaControl/Pipeline/VideoPipeline.cs` (one call site).
- **Behavior changed:** No. Same snapshot semantics, same iteration, same acceptability check.
- **Debug build:** Pass (0 warnings, 0 errors).
- **Release build:** Pass (0 warnings, 0 errors).
- **WPF boundary check:** Pass.
- **Presentation parity check:** Pass.
- **Stale `DxCdg` search:** Clean (0 source results; 1 doc-only match in this roadmap log).
- **Runtime/manual validation:** Not yet completed. Needed: seek/scrub stress on video, video with
  audio, video without audio, and background video seek. Confirm seek responsiveness and target
  frame selection are unchanged.
- **Plan updated:** Yes.

**Latest review findings — 2026-07-02 (per-frame VideoProcessorStream allocation):**

- **Readiness:** Proceed with exclusions. The CDG wash-color and seek-frame fixes are done. This review
  targets the next smallest safe item: the per-frame `VideoProcessorStream[]` allocation in
  `D3D11VideoProcessorPresenter.BlitTextureToPresentation`. A new single-element array is allocated on
  every video blit call (per frame). The struct fields `Enable`, `OutputIndex`, `InputFrameOrField` are
  constant; only `InputSurface` changes per call.
- **Safety/Risk:** Low. `D3D11VideoProcessorPresenter` is `internal sealed` — not public API.
  `BlitTextureToPresentation` has one call site (`PresentHardwareFrame` in the same class). The D3D11
  immediate context is single-threaded; all D3D11 calls are already serialized, so a reusable field
  array is safe. The fix: add a `VideoProcessorStream[] _streams` field initialized once with constant
  values, update `_streams[0].InputSurface = inputView` before `VideoProcessorBlt`, and null
  `InputSurface` in `Reset()`. `VideoProcessorStream` is a struct, so in-place field update is safe.
  The previous `inputView` is disposed by its `using` block before the next call, so no stale COM
  reference is held.
- **Excluded from auto-execute:**
  - `Marshal.SizeOf<CdgShaderConstants>()` cache in `D3D11MediaCompositionPresenter`: touches the
    composition path, which is excluded (composition layer churn needs separate narrowed review).
  - `new VideoFrameEventArgs` per presented frame: risky because event handlers may retain references
    to the args instance. Requires separate review of host EventArgs semantics.
  - Composition layer churn, CDG snapshot pooling: same as prior reviews.
- **Auto-execute allowed:** Yes, for the VideoProcessorStream[] reuse only.
- **Recommendation:** Proceed with exclusions.

**Execution — 2026-07-02 (VideoProcessorStream[] reuse):**

- **Change:** Replaced the per-frame `VideoProcessorStream[]` allocation in
  `D3D11VideoProcessorPresenter.BlitTextureToPresentation` with a reusable `_streams` field
  initialized once. The struct fields `Enable`, `OutputIndex`, `InputFrameOrField` are constant;
  only `InputSurface` is updated per call before `VideoProcessorBlt`. `InputSurface` is nulled in
  `Reset()` to avoid holding a stale COM reference. `VideoProcessorStream` is a struct, so in-place
  field update is safe. D3D11 immediate context is single-threaded.
- **Files changed:** `FatzBombMedia.MediaControl/Core/Graphics/D3D/Video/D3D11VideoProcessorPresenter.cs`.
- **Behavior changed:** No. Same Blt call, same stream parameters, same rect/clear/flush sequence.
- **Debug build:** Pass (0 warnings, 0 errors).
- **Release build:** Pass (0 warnings, 0 errors).
- **WPF boundary check:** Pass.
- **Presentation parity check:** Pass.
- **Stale `DxCdg` search:** Clean (0 source results).
- **Runtime/manual validation:** Not yet completed. Needed: video playback with audio, video without
  audio, hardware video path, seek/scrub, secondary output, and CDG+video background composition.
  Confirm VideoProcessorBlt output is visually unchanged.
- **Plan updated:** Yes.

**Latest review findings — 2026-07-02 (remaining items — review only, no source edits):**

- **Readiness:** Proceed with exclusions. Three items are done (CDG wash-color, seek-frame
  inspection, VideoProcessorStream[] reuse). This review narrows the remaining candidate work into
  individually assessable sub-items so future execution can pick the safe ones without bundling.
- **Item A — `Marshal.SizeOf<CdgShaderConstants>()` cache:**
  - **Safety/Risk:** Low. `CdgShaderConstants` is a `private struct` with `[StructLayout(LayoutKind.Sequential)]`
    containing 3 × `Vector4` = 48 bytes. `Marshal.SizeOf<T>()` is a reflection-based call that returns the
    same value every call for the same type. It is used in `SetQuadConstants` (hot path, per CDG overlay
    draw) and `EnsureDrawPipeline` (cold path, once). Caching as a `private static readonly int` field
    eliminates the per-draw reflection call. No layer ordering, dirty-region, rect, or blend behavior
    changes. The prior auto-execute review was overly conservative by excluding this as "composition
    path" — it is a pure size-constant cache, not a layer/dirty/capture change.
  - **Files:** `D3D11MediaCompositionPresenter.cs` (one new field, two call sites changed).
  - **Auto-execute:** Yes. Risk Low. Bounded scope, no architecture decision, no second opinion needed.
  - **Recommendation:** Proceed.
- **Item B — Composition layer churn (sub-item breakdown):**
  - **Safety/Risk:** Medium overall, but narrows to Low for individual sub-items. The composition
    present path has multiple allocation sources that need individual assessment:
    - **B1 — Target list reuse:** `targets.Where(...).ToList()` (lines 241-243) and
      `validTargets.Where(...).ToList()` (lines 252-254) each allocate a `List<>` + `Where` iterator
      per present. The presenter is `internal sealed` and single-threaded (D3D11 immediate context).
      `validTargets` is only used within `PresentToTargets` (passed to `Capture` via `Select`, then
      filtered into `presentationTargets`); `presentationTargets` is used in the draw `foreach`
      (line 299) and `presentationTargets[0]` (line 317). Neither escapes the method. `drawnTargets`
      (line 298, pre-sized) is returned via `out presentedTargets` (line 322) — must stay as a fresh
      allocation. Fix: add `_scratchValidTargets` and `_scratchPresentationTargets` fields, clear at
      method entry, replace `.Where().ToList()` with `foreach` + `Add`. The `Select` iterator at
      line 250 for `_targetRegistry.Capture` is one small allocation — leave it for now.
      **Risk: Low. Safe for auto-execute.**
    - **B2 — `EnumerateLayers()` accessor methods:** `EnumerateLayers()` is a `public` yield iterator
      on `MediaCompositionFrame` (`public readonly partial record struct`). It's called 3× per
      present: `Capture` (line 35, needs all layers), `EnsureCompositionBackgroundFrame:347`
      (`FirstOrDefault(Background)`), `EnsureCompositionOverlayFrame:479` (`FirstOrDefault(Overlay)`).
      Each call allocates an enumerator + up to 3 `MediaCompositionLayer` records (sealed record,
      reference type). The overlay `FirstOrDefault` is worst — it iterates through background + wash
      before reaching overlay, allocating 1-2 throwaway records. Fix: add `internal` methods
      `TryGetBackgroundLayer()` and `TryGetOverlayLayer()` to `MediaCompositionFrame` (using `partial`)
      that construct a single `MediaCompositionLayer` directly without the yield iterator. Replace
      the two `FirstOrDefault` calls. Leave `EnumerateLayers()` unchanged for `Capture`. All callers
      of `EnumerateLayers()` are internal to this assembly (grep confirmed: `D3D11CompositionLayerCache`
      + two `D3D11MediaCompositionPresenter` sites). The `internal` accessors are additive, non-breaking,
      and don't expand the public NuGet API surface. **Risk: Low. Safe for auto-execute.** Main risk:
      logic divergence between `EnumerateLayers()` and the accessors. Mitigation: the accessors should
      check the same flags (`HasBackground`/`HasD3D11Background`/`HasRetainedBackground`,
      `HasCdgOverlay`) and construct identical descriptors.
    - **B3 — `CreateUpdatePlan()` LINQ elimination:** Both `D3D11CompositionLayerCache.CreateUpdatePlan()`
      (lines 61-65) and `D3D11CompositionTargetRegistry.CreatePlan()` (lines 47-51) build
      `Where().OrderBy().ThenBy().Select()` chains. The plan constructors do `[.. updates]` (array
      spread). Fix: add `_scratchUpdates` (`List<D3D11CompositionLayerUpdate>`) field on the cache
      and `_scratchTargetUpdates` (`List<D3D11CompositionTargetUpdate>`) on the registry. Replace
      LINQ chains with `foreach` + `Sort`. The sort must match `OrderBy(Order).ThenBy(Id)` for layers
      and `OrderBy(Kind).ThenBy(Id, Ordinal)` for targets. `D3D11CompositionLayerUpdate` and
      `D3D11CompositionTargetUpdate` are both `readonly record struct` — value types, so `List<>`
      entries are inline. The plan constructors still do `[.. updates]` (array copy) — unavoidable
      without changing the plan constructor, but the LINQ iterator chain is eliminated. **Risk: Low.
      Safe for auto-execute.**
    - **B4 — `_layers.Keys.ToArray()` in `Capture:43`:** This is a necessary snapshot — the loop body
      calls `state.Clear()` which modifies dictionary state. Do NOT remove without redesigning the
      capture loop to use separate removal lists.
    - **B5 — `drawnTargets` list (line 297):** Already sized correctly
      (`new List<>(presentationTargets.Count)`). Returned as `presentedTargets` via `out` parameter, so
      cannot be a reusable field without copying. Leave as-is.
  - **Files:** B1: `D3D11MediaCompositionPresenter.cs`. B2: `MediaCompositionFrame.Layers.cs` (or new
    partial file), `D3D11MediaCompositionPresenter.cs`. B3: `D3D11CompositionLayerCache.cs`,
    `D3D11CompositionTargetRegistry.cs`.
  - **Auto-execute:** Yes for B1, B2, B3, B4 individually. No for B5.
  - **Recommendation:** Proceed. Execute B1, B2, B3, B4. Each is Low risk, bounded, no architecture
    decision, no second opinion needed.

**Execution — 2026-07-02 (B1+B2+B3+B4 — composition layer churn):**

- **B1 — Target list reuse:** Replaced two `.Where().ToList()` allocations in `PresentToTargets` with
  reusable `_scratchValidTargets` / `_scratchPresentationTargets` fields cleared at method entry and
  in `Reset()`. Neither list escapes the method; `drawnTargets` (returned via `out`) stays as a fresh
  allocation.
- **B2 — Layer accessor methods:** Added `internal TryGetBackgroundLayer()` and
  `TryGetOverlayLayer()` to `MediaCompositionFrame` (partial record struct). These construct a single
  `MediaCompositionLayer` directly without the yield iterator, replacing the two
  `EnumerateLayers().FirstOrDefault(...)` calls in the presenter. `EnumerateLayers()` left unchanged
  for `Capture`. Accessors are `internal` — no public NuGet API change.
- **B3 — LINQ elimination:** Replaced `Where().OrderBy().ThenBy().Select()` chains in both
  `D3D11CompositionLayerCache.CreateUpdatePlan()` and `D3D11CompositionTargetRegistry.CreatePlan()`
  with `foreach` + `Sort` into reusable scratch lists. Sort comparers match the original ordering:
  layers by `Order` then `Id`, targets by `Kind` then `Id` (ordinal). Both update types are
  `readonly record struct` (value type, inline in lists).
- **B4 — Remove `Keys.ToArray()` snapshot:** Replaced `_layers.Keys.ToArray()` in
  `D3D11CompositionLayerCache.Capture` with direct `_layers.Keys` enumeration. The loop body calls
  `state.Clear()` (modifies the state object, not the dictionary), so no dictionary mutation occurs
  during iteration.
- **Files changed:**
  - `MediaCompositionFrame.Layers.cs` (B2: two new accessor methods)
  - `D3D11MediaCompositionPresenter.cs` (B1: scratch list fields + two Where().ToList() replacements
    + Reset() clearing; B2: two FirstOrDefault call site replacements)
  - `D3D11CompositionLayerCache.cs` (B3: scratch list + CreateUpdatePlan LINQ replacement + Reset()
    clearing; B4: Keys.ToArray removal)
  - `D3D11CompositionTargetRegistry.cs` (B3: scratch list + CreatePlan LINQ replacement + Reset()
    clearing)
- **Behavior changed:** No. Same target filtering, same layer ordering, same plan contents, same
  capture semantics.
- **Debug build:** Pass (0 warnings, 0 errors).
- **Release build:** Pass (0 warnings, 0 errors).
- **WPF boundary check:** Pass.
- **Presentation parity check:** Pass.
- **Runtime/manual validation:** Not yet completed. Needed: audio+CDG, standalone CDG, CDG+image
  background, CDG+video background, secondary/projector output, resize/fullscreen, media-type
  switching, and stop/clear. Confirm layer order, dirty regions, and multi-target fan-out are
  visually unchanged.
- **Plan updated:** Yes.
- **Item C — `new VideoFrameEventArgs` per presented frame:**
  - **Safety/Risk:** Medium-High. `VideoFrameEventArgs` carries per-frame `PixelData` payloads
    (`ReadOnlyMemory<byte>` or `byte[]`). Event handlers in `NativeMediaPresentationCoordinator` may
    retain references. Reusing the args instance would require clearing the payload and would be risky
    if any handler stores the reference. The args are allocated in `VideoPipeline.cs:1706` and
    `PipelineMediaEngine` (lines 371, 392, 561, 584, 607).
  - **Auto-execute:** No. Requires separate review of host EventArgs semantics and handler retention.
  - **Recommendation:** Do not proceed. Defer until EventArgs retention audit is done.

**Latest review findings — 2026-07-02 (Item C — review only, no source edits):**

- **Readiness:** Do not proceed. After thorough handler-retention analysis, Item C should be deferred
  permanently unless the `VideoFrameEventArgs` API is redesigned.
- **Handler retention analysis:**
  - **Video frames** (`VideoPipeline.PresentFrame:1706`): Handler `OnVideoFrameReady` calls
    `PresentPrimaryVideoFrame(e)` or `PresentVideoFrameAsCdgBackground(e)`. Both read fields
    (`PixelData`, D3D11 pointers, metadata) and either copy pixel data into
    `_compositionBackgroundCopyBuffer` or use hardware pointers immediately. The `VideoFrameEventArgs`
    reference is NOT retained after the handler returns. **Reuse is theoretically safe.**
  - **Background frames** (`PipelineMediaEngine.RaiseBackgroundFrame:371,392,607`): Handler
    `OnCoreBackgroundFrameReady` follows the same pattern — reads fields, copies/uses immediately,
    does NOT retain the args. **Reuse is theoretically safe.**
  - **Image frames** (`PipelineMediaEngine.RaiseImageFrame:561,584`): The args IS retained —
    `_currentImageFrame = args` stores it, and `TryGetCurrentImageFrame` returns it to the coordinator
    later (line 424). **Reuse is NOT safe for image frames.**
- **Blocking constraint — `init` setters:** `VideoFrameEventArgs` uses `init` properties:
  `PixelData { get; init; }`, `Width { get; init; }`, etc. `init` setters can only be set during
  object initialization and cannot be mutated after construction. Reusing an existing instance by
  updating its fields is impossible without changing `init` to `set` — which is a public API change
  on a `public sealed class` in a packable NuGet package. Changing `init` to `set` would allow event
  handlers to mutate the args they receive, which is a semantic contract change.
- **Cost-benefit assessment:** The `VideoFrameEventArgs` object is a single small reference-type
  allocation (~10 value-type fields, no embedded arrays — `PixelData` is a `ReadOnlyMemory<byte>` over
  the decoder's existing buffer). At 60fps, that's 60 small Gen0 allocations per second — negligible
  compared to the allocations already eliminated in B1-B4 (64KB CDG snapshots, target lists, LINQ
  chains, yield iterators). The API risk of changing `init` to `set` outweighs the allocation savings.
- **Auto-execute:** No. Deferred permanently unless `VideoFrameEventArgs` is redesigned with mutable
  properties (a Stage 5 architecture decision, not a Stage 1 allocation fix).
- **Recommendation:** Do not proceed. Item C is closed for Stage 1. The per-frame allocation is
  acceptable for a Gen0-collected small object.
- **Item D — CDG snapshot pooling (formerly Stage 1D):**
  - **Safety/Risk:** High. Buffer lifetime/aliasing risk. Never auto-execute per command rules.
  - **Recommendation:** Do not proceed. Requires human approval and separate review.

**Latest review findings — 2026-07-02 (Item D — review only, no source edits):**

- **Readiness:** Do not proceed. Item D should remain deferred permanently unless the CDG snapshot
  lifecycle is redesigned as a Stage 5 architecture change, not a Stage 1 allocation fix.
- **Allocation profile:** `CdgStateMachine.CreateSnapshot` copies `_colorIndices.ToArray()` (64,800
  bytes) + `_paletteArgb.ToArray()` (256 bytes) + dirty-rect array per snapshot. However, snapshots
  are NOT created per frame — they're created on CDG state changes (lyric updates, ~1/sec during
  active lyrics) and skipped entirely on retained-refresh presents (`_lastCdgSnapshot` reuse path at
  line 776). The CDG wash-color fix already eliminated the most frequent snapshot allocation. The
  remaining rate is ~65KB/sec during active lyrics — small and Gen0-collected.
- **Why pooling is high-risk:**
  - **Retained-refresh path:** `_lastCdgSnapshot` retains the snapshot across present calls
    (line 801). Its arrays are read again in later presents (line 776 → `snapshot = _lastCdgSnapshot`
    → `BuildCompositionFrameNoLock(snapshot, ...)` → `UploadCdgSnapshot` reads
    `snapshot.ColorIndices.Span`). If pooled arrays were returned between presents, the data would be
    overwritten by the next renter — causing stale lyrics or palette corruption.
  - **`MediaCompositionFrame` retention:** The snapshot is embedded in the frame as `CdgOverlay`
    (`MediaCompositionFrame.cs:37`). The frame is a `readonly record struct` passed by value; multiple
    copies share the same snapshot reference. Ensuring pooled arrays are returned exactly once after
    all copies are consumed is error-prone.
  - **`CdgRenderSnapshot` is `public sealed class`** with `ReadOnlyMemory<byte>` properties. Pooling
    would require `IDisposable` or a `Release()` method, and the coordinator would need to release the
    previous `_lastCdgSnapshot` before replacing it — but the retained-refresh path reads the previous
    snapshot, so release timing must happen AFTER the retained refresh, not before.
  - **Failure mode:** Use-after-release or double-release causes silent visual corruption (stale
    lyrics, wrong palette) that is very hard to diagnose.
- **Alternative approaches (all Stage 5 architecture, not Stage 1):**
  - Dirty-only snapshot: pass only changed regions instead of full 64KB copy. The upload path already
    uses dirty regions (`UploadCdgIndexRegions`), but `CreateSnapshot` still copies the full array.
  - Double-buffering: keep two `_colorIndices` arrays, swap on snapshot. Avoids copy but doubles
    memory and requires swap synchronization.
  - Zero-copy: pass `ReadOnlyMemory<byte>` over the live array under the lock. Unsafe — the GPU
    upload thread could see partial updates.
- **Cost-benefit:** ~65KB/sec during active lyrics (not per-frame), Gen0-collected. The risk of
  pooling (use-after-release → visual corruption) far outweighs the allocation savings. The CDG
  wash-color fix already eliminated the most frequent snapshot allocation.
- **Auto-execute:** No. Never. Requires human approval.
- **Recommendation:** Do not proceed. Item D is closed for Stage 1. If CDG allocation becomes a
  measured bottleneck, revisit as a Stage 5 architecture change using dirty-only snapshots or
  double-buffering — not ArrayPool pooling of the current immutable snapshot.
- **Overall recommendation:** Proceed with Item A (Marshal.SizeOf cache) only. All other remaining
  items need separate narrowed review or human approval.

**Execution — 2026-07-02 (Item A — Marshal.SizeOf cache):**

- **Change:** Added `private static readonly int CdgShaderConstantsSize = Marshal.SizeOf<CdgShaderConstants>()`
  field in `D3D11MediaCompositionPresenter`. Replaced the two `Marshal.SizeOf<CdgShaderConstants>()`
  call sites (`SetQuadConstants` hot path and `EnsureDrawPipeline` cold path) with the cached field.
  Eliminates the per-draw reflection call. No layer/dirty/rect/blend behavior changes.
- **Files changed:** `FatzBombMedia.MediaControl/Core/Graphics/Composition/D3D11MediaCompositionPresenter.cs`.
- **Behavior changed:** No. Same size value (48 bytes for 3 × Vector4), same stackalloc, same buffer
  description.
- **Debug build:** Pass (0 warnings, 0 errors).
- **Release build:** Pass (0 warnings, 0 errors).
- **WPF boundary check:** Pass.
- **Presentation parity check:** Pass.
- **Runtime/manual validation:** Not yet completed. Needed: audio+CDG overlay draw, standalone CDG,
  CDG+video background, secondary output. Confirm CDG overlay rendering is visually unchanged.
- **Plan updated:** Yes.

### 2. Media Loading, Reliability, Async, and Error Reporting

Goal: improve correctness and diagnosability without changing media behavior.

Useful context retained from the full analysis:

- The most urgent media-loading regressions in this area have source fixes. Validate those paths before
  broad logging or error-surface work.
- Standalone image display uses the dedicated Core path: `PipelineMediaEngine.OpenImage` decodes or
  accepts BGRA input, raises `ImageFrameReady`, and the presentation coordinator retains/presents that
  image frame.
- Sidecar/background discovery is intentionally split between explicit backgrounds and automatic
  sidecar/archive resolution. Preserve the rule that discovered sidecars can take precedence unless
  `ignoreSidecar` is true.
- The existing silent scanner catches are probably acceptable behaviorally but poor diagnostically.
  Add low-noise diagnostics rather than changing skip-on-access-failure behavior.

Candidate work:

- Keep BUG-BGVIDEO-1 as research-only unless fresh `[BackgroundVideo]` logs prove a specific restart,
  PTS, frame-lifetime, or presentation-timing failure mode.
- Investigate device-removed/device-reset handling at the D3D11 present boundary before adding a
  recovery path.
- Add structured logging and a typed host-facing error surface for high-value failures: native DLL
  load, file open/load, archive scan, FFmpeg/BASS open/decode, background media load, backend
  selection, and device-loss conditions.

Primary files:

- `FatzBombMedia.MediaControl/Pipeline/CdgPipeline.cs`
- `FatzBombMedia.MediaControl/Core/Audio/AudioEngine.cs`
- `FatzBombMedia.MediaControl/Core/Audio/AudioNormalizer.cs`
- `FatzBombMedia.MediaControl/Core/Files/DirectoryScanner.cs`
- `FatzBombMedia.MediaControl/Core/Graphics/D3D/SwapChain/D3D11SwapChainPresenter.cs`
- `FatzBombMedia.MediaControl/Api/MediaControlPlayer.cs`
- `FatzBombMedia.MediaControl/Pipeline/PipelineMediaEngine.cs`
- `FatzBombMedia.MediaControl/Controllers/MediaFile.cs`
- `FatzBombMedia.MediaControl/Controllers/MediaBackgroundResolver.cs`
- `FatzBombMedia.MediaControl/Core/Graphics/NativeMediaPresentationCoordinator.cs`

Validation focus: repeated CDG play/seek, normalization, inaccessible directories, missing native DLLs,
unsupported/corrupt media, standalone image display, WPF/BGRA image load, zipped MP3+CDG with sidecar,
separate MP3+CDG with sidecar, explicit background override, `ignoreSidecar`, and normal playback log
noise.

Stage guard rails:

- Validate media-loading regression fixes before broad structured logging work.
- Do not change archive pairing, explicit background precedence, or sidecar precedence without a focused
  before/after matrix.
- Do not route image-only playback through the WPF adapter; the fix should preserve the Core image
  frame path and WPF boundary.
- Do not add noisy per-frame logs while improving diagnostics.

**Current Stage 2 status - 2026-07-03:**

- **Complete for current roadmap pass:** BUG-IMAGE-1, BUG-SIDECAR-1, BUG-CDG-BG-1, explicit background
  arrays, stopped standalone visuals, no-audio visual/tempo behavior, standalone-CDG CTS disposal, Core
  `.ConfigureAwait(false)` coverage, and low-noise scanner diagnostics are implemented and validated.
- **Manual validation passed:** standalone image, standalone no-audio video, standalone no-audio CDG,
  sidecar image/video + CDG first-frame sync, ZIP MP3+CDG sidecars, separate MP3+CDG same-base sidecars,
  explicit background arrays, `ignoreSidecar`, and known working sidecar/background video loops.
- **Deferred:** BUG-BGVIDEO-1 is limited to one known AVI sidecar repro and is research-only for now.
  Other tested sidecar/background videos work and should remain the regression priority.
- **Deferred:** device-loss investigation and broad structured error/logging are not blocking the current
  pass. Reopen only as explicit focused stages.

**BUG-BGVIDEO-1 (deferred sidecar/background video research) - 2026-07-02:**

- **Current status:** Deferred. One known AVI sidecar (`D:\- Testing Files\karaoke-inner-images.avi`)
  can appear to loop around ~1s when used as background for `D:\- Testing Files\karaoke-inner-images.zip`.
  Other tested sidecar/background videos loop correctly and are the higher-priority behavior to preserve.
- **Diagnostics now available:** `[BackgroundVideo]` `Trace.WriteLine` logs capture open metadata,
  first-frame data, periodic iteration PTS, decode false, no-frame-before-restart, restart, and `Seek(0)`
  events in `%TEMP%\FatzBombMedia\player-debug.log`.
- **Research trigger:** Reopen only with a fresh repro log for the failing AVI showing whether restart
  occurs around 1s, EOF arrives early, PTS jumps/stalls, or the issue is presentation-rate rather than
  restart timing.
- **Primary files:** `PipelineMediaEngine.cs`, `VideoDecoder.cs`, `VideoPresentationTiming.cs`.
- **Guard rails:** Do not change decoder selection, hardware-frame output policy, or sidecar timing for
  all backgrounds to fix this single file without targeted evidence.

**Execution - 2026-07-02 (Stage 2 behavior fixes):**

- **BUG-IMAGE-1 fixed:** `MediaFile.ApplyCoreBackground` avoids redundant null-background clearing, Core
  retains/raises standalone image frames, `NativeMediaPresentationCoordinator` presents images through
  the Core composition path, and `MediaSurface` keeps stopped standalone visuals visible without reading
  WPF dependency properties from render/background threads.
- **BUG-SIDECAR-1 fixed:** `MediaBackgroundResolver` collects all matching auto-detected background
  candidates and randomly chooses one. Archive candidates include any inner image/video entry plus any
  external sidecar matching the ZIP file base name. Non-archive candidates include all external
  image/video sidecars matching the loaded file base name.
- **Explicit background array fixed:** Added concrete `MediaControlPlayer.Load(string path,
  IReadOnlyList<string> backgroundPaths, bool ignoreSidecar = false)` and matching `LoadAsync` overloads.
  Invalid/non-image/non-video paths are ignored; if at least one valid explicit path remains, one is
  selected at random and used as the explicit fallback behind auto-detected sidecars.
- **No-audio visual/tempo fixes:** stopped standalone no-audio video/CDG can present initial visuals,
  video current-frame reissue works for attach/resize/display-mode refreshes, and clock-backed tempo now
  routes through `ClockAudioEngine` / `PlaybackClock` for video/CDG without audio.
- **Sidecar/CDG first-frame sync fixed:** sidecar/background frames now wait for a visible CDG snapshot
  instead of treating reset-only CDG revisions as readiness.
- **Reliability cleanup fixed:** standalone-CDG completion monitor CTS disposal, Core async
  `.ConfigureAwait(false)` coverage, and low-noise `DirectoryScanner` diagnostics are complete.
- **Behavior changed:** Yes, intentionally. Auto-detected sidecars are now randomly selected from a flat
  valid-candidate pool. Existing no-background loads still clear prior background state before open.
- **Validation:** Debug build, Release build, WPF boundary check, and presentation parity check passed
  after the source fixes. Later Release builds may warn if `FatzBombMedia.Player` or Visual Studio locks
  output DLLs; close the running player for warning-free Release validation.
- **Manual validation:** Passed on 2026-07-03 for ZIP MP3+CDG sidecars, separate MP3+CDG sidecars,
  explicit background path arrays/fallback, `ignoreSidecar`, standalone image, standalone no-audio
  video/CDG, sidecar image/video + CDG first-frame sync, and known working sidecar/background videos.

### 3. Threading, State, and Public API Cleanup

Goal: reduce latent coupling and blocking while preserving public behavior.

Useful context retained from the full analysis:

- `ClockAudioEngine` is live and intentional for standalone CDG and video-without-audio. It already
  exposes `SetTempo` through `PlaybackClock`; clock-backed tempo routing is now wired through
  `MediaEffects` for video/CDG without audio.
- `MediaEffects` should continue to keep volume, pitch, and normalization audio-only unless a future API
  explicitly makes those controls meaningful for clock-only media.
- Public events can fire from media/background threads. Clarify that contract before changing event
  delivery or adding automatic marshaling.
- The operation queue's blocking async pattern is isolated but sensitive: preserve operation ordering and
  load serialization if it is converted to an async pump.

**Next Stage 3A start - 2026-07-03:**

Map `MediaControlOperationQueue` before editing source. Document its callers, operation ordering rules,
blocking waits, exception/cancellation behavior, and UI-thread implications. The output should be a
small staged implementation plan for an async worker model, not an immediate refactor.

Candidate work:

- Preserve the validated clock-backed tempo behavior for video without audio and standalone CDG without
  audio. Audio-backed tempo should continue to use the real BASS tempo path.
- Replace `MediaControlOperationQueue` blocking async work (`GetAwaiter().GetResult()`) with an async
  worker model that preserves operation ordering.
- Clarify public event threading behavior. Core events may fire from background media threads; adapters
  or hosts must marshal to UI threads unless a future API explicitly changes that contract.
- Centralize final public playback state ownership so pipelines report observations instead of racing to
  mutate final state independently.
- Decide whether unused public/internal-looking members are intentional API or removable, especially
  `IMediaEngine.SeekAsync` and unread `CompatibilityInfo` members.

Primary files:

- `FatzBombMedia.MediaControl/Core/Threading/MediaControlOperationQueue.cs`
- `FatzBombMedia.MediaControl/Api/MediaControlPlayer.cs`
- `FatzBombMedia.MediaControl/Controllers/MediaFile.cs`
- `FatzBombMedia.MediaControl/Controllers/MediaEffects.cs`
- `FatzBombMedia.MediaControl/Pipeline/ClockAudioEngine.cs`
- `FatzBombMedia.MediaControl/Pipeline/PlaybackClock.cs`
- `FatzBombMedia.MediaControl/Pipeline/PlaybackCoordinator.cs`
- `FatzBombMedia.MediaControl/Pipeline/AudioPipeline.cs`
- `FatzBombMedia.MediaControl/Pipeline/CdgPipeline.cs`
- `FatzBombMedia.MediaControl/Pipeline/VideoPipeline.cs`
- `FatzBombMedia.MediaControl/Core/State/MediaState.cs`
- `FatzBombMedia.MediaControl/Core/Abstractions/IMediaEngine.cs`
- `FatzBombMedia.MediaControl/Core/Files/BinaryCompatibility.cs`

Validation focus: rapid load/cancel/reload, repeated play/pause/seek/stop, event ordering, public state
transitions, file scanning, tempo changes before and after load, tempo while playing, tempo while paused,
video-without-audio timing, standalone CDG timing, and packable API compatibility decisions.

Stage guard rails:

- Do not make volume, pitch, or normalization appear supported for clock-only media unless that is a
  deliberate API change.
- Do not break audio-backed tempo. Audio files and video-with-audio should continue to use the real BASS
  tempo path.
- Do not convert operation-queue behavior and playback-state ownership in the same patch. They are both
  sequencing-sensitive.
- Stop if an API cleanup could affect external NuGet consumers; decide whether to keep, obsolete, or
  remove explicitly.

### 4. Native Lifetime and FFmpeg Safety Audit

Goal: reduce hard-crash risk around unsafe/native ownership.

Useful context retained from the full analysis:

- Native/unsafe bugs can hard-crash the host, so this stage is audit-first. Do not start by wrapping
  everything.
- Hardware-frame ownership, software-frame return paths, decoder close/error paths, and temp-file cleanup
  are the important seams.
- The AV1 fallback observed during VT-F validation is currently classified as GPU/driver capability, not
  a decoder-selection bug.

Candidate work:

- Audit every `AVFrame*`, `AVPacket*`, `AVCodecContext*`, hardware frame, temp-file, and decoder
  close/error path.
- Document ownership transfer rules before changing wrappers.
- Add narrow SafeHandle-style wrappers only where they reduce risk without broad decoder behavior
  changes.
- Stress repeated load/close, corrupt media, unsupported codec, seek/scrub, hardware decode on/off,
  video with audio, and video without audio.

Primary files:

- `FatzBombMedia.MediaControl/Core/Video/VideoDecoder.cs`
- `FatzBombMedia.MediaControl/Core/Audio/VideoAudioEngine.cs`
- FFmpeg hardware-frame ownership helpers and related cleanup paths.

Validation focus: repeated load/close, corrupt media, unsupported media, hardware decode on/off, AV1
fallback behavior, rapid seek/scrub, video with audio, video without audio, and long-run playback.

Stage guard rails:

- Do not change decoder selection, codec preference, or fallback policy unless a concrete lifetime bug
  requires it.
- Do not introduce managed wrappers that obscure who owns an FFmpeg pointer at each transfer point.
- Stop if ownership cannot be proven from source and stress behavior; document the uncertainty first.

### 5. Architecture and Maintainability Refactors

Goal: improve boundaries after smaller reliability and safety work is stable.

Useful context retained from the full analysis:

- `RenderPipeline` currently has a concrete `VideoPipeline` cast. Remove it only through a narrow
  internal seam; do not pollute public `IVideoEngine` with renderer-specific methods.
- `ClockAudioEngine` is broad because it stands in for audio timing on clock-backed media. The Stage 3
  tempo bug is fixed; any future split should be treated as architecture cleanup, not behavior repair.
- `VideoPipeline.cs` is still large, but splitting it is risky. Extract one responsibility at a time and
  keep behavior identical after each extraction.
- The two D3D11 frame presenter classes are a deliberate wrapper pair. Collapse them only if it reduces
  real complexity and the hardware-frame path remains unchanged.

Candidate work:

- Remove the `RenderPipeline` to concrete `VideoPipeline` cast without adding renderer-specific
  members to public `IVideoEngine`.
- Decide whether `ClockAudioEngine` should remain a documented virtual `IAudioEngine` adapter or be
  split behind narrower clock/transport/effects/file-audio interfaces.
- Split `VideoPipeline.cs` one responsibility at a time: seek pump, frame queue, decode loop,
  presentation loop, timing constants, diagnostics.
- Clean up stale `VorticeD3D11Renderer` doc-crefs and legacy `[VorticeD3D11...]` log prefixes.
- Optionally collapse the two D3D11 frame-presenter wrapper classes only if it reduces complexity
  without changing behavior.

Primary files:

- `FatzBombMedia.MediaControl/Pipeline/RenderPipeline.cs`
- `FatzBombMedia.MediaControl/Pipeline/VideoPipeline.cs`
- `FatzBombMedia.MediaControl/Core/Abstractions/IVideoEngine.cs`
- `FatzBombMedia.MediaControl/Core/Audio/ClockAudioEngine.cs`
- `FatzBombMedia.MediaControl/Core/Abstractions/IAudioEngine.cs`
- D3D11 presentation/comment/log-prefix files.

Validation focus: boundary scripts, video render-at-current-position, video-without-audio,
standalone CDG, audio+CDG, CDG+video background, secondary output, and seek/scrub.

Stage guard rails:

- Do not use architecture cleanup to fix behavior bugs that belong in Stages 2 or 3.
- Do not broaden WPF adapter knowledge of Core renderer, pipeline, internal CDG, video-frame, or D3D11
  types.
- Do not combine `VideoPipeline` splitting with timing changes, allocation pooling, or public API
  cleanup.
- Run Graphify/source mapping before and after boundary changes to verify coupling decreased.

### 6. Optional Test/CI Upgrade

Goal: move beyond the current practical guard rails when the project needs CI-grade regression
protection.

Useful context retained from the full analysis:

- Current day-to-day guard rails are builds, boundary/parity scripts, and manual Player smoke testing.
  That is accepted for now, but it is not CI-grade regression protection.
- Pure logic tests should come before native/GPU tests. Avoid tests that require BASS, FFmpeg DLLs, a
  GPU, a visible WPF window, or exact display timing unless they are clearly marked integration tests.
- Recently fixed behavior is a good candidate for future tests if the logic can be isolated: tempo
  routing for clock-backed media, image-only load event/presentation state, sidecar/archive background
  resolution, and explicit background-list selection.

Candidate work:

- Add deterministic tests for pure logic first: CDG state-machine packet behavior, archive pairing,
  file type detection, sidecar/background resolution, configuration validation, and `MediaState`
  transitions.
- Add a one-command local verification script that runs restore/build/Release/boundary/parity/tests.
- Add CI only after local checks are stable and native/GPU-dependent tests are excluded or isolated.

Do not make tests depend on native DLLs, GPUs, display timing, or WPF windows unless they are explicitly
integration tests.

## Future: Seamless Primary Video Looping

Status: possible future polish; not blocking current sidecar/background work.

Primary/standalone video looping currently works and preserves the visual surface, but some files still
show a small perceptible hold/frame jump at the loop boundary. Background/sidecar video remains the more
important product path and should not be destabilized while polishing standalone loop cadence.

Observed context from 2026-07-02 loop investigation:

- Primary video now restarts inside `VideoPipeline` before the public `PlaybackEnded` path, avoiding the
  earlier stop/play blanking behavior.
- Loop restart can present a cached CPU/BGRA start-frame snapshot and may also decode the first loop frame
  immediately (`snapshot=True`, `decodedFirst=True`), yet a small perceived pause can remain.
- This suggests the remaining issue is not a simple WPF surface gap. Likely contributors are audio seek/
  restart discontinuity, single-decoder seek/reset cost, or unavoidable content discontinuity between the
  file's final and first frames.

Future approaches, if seamless primary looping becomes a hard requirement:

- Centralize end-of-media/loop policy so primary video, audio/CDG, and sidecar/background paths share the
  same loop-vs-complete decision while each pipeline still applies its own transport mechanics.
- Investigate audio-specific loop handling for `VideoAudioEngine` instead of seeking/restarting audio at
  EOF on the presentation thread.
- Consider a pre-roll strategy: retain a short CPU prefix, pre-decode loop-start frames, or use a second
  decoder for loop-start warmup. Do not attempt hardware-frame caching without a clear ownership model.
- Keep sidecar/background video behavior as the regression priority. Any shared helper must preserve the
  working sidecar loop path and not introduce public API changes unless explicitly approved.

Guard rails:

- Do not treat `AVStream.nb_frames` as authoritative; it is often missing or inaccurate for containers,
  VFR media, and hardware decode paths.
- Do not merge this with display-rate compositor-clock work. Loop transport and compositor cadence are
  related symptoms but different responsibilities.
- Validate with both video-with-audio and video-without-audio, plus known working sidecar/background
  videos, before accepting any future change.

## Future: Display-Rate Compositor Clock

Status: not implemented.

The remaining structural timing gap is that video presentation is paced by a source-cadence software
timer while DWM samples the surface on display vblank. Those clocks are not phase-locked. A future
display-rate compositor-clock model could align presentation to vblank and resample video/CDG layers
onto the display grid, producing uniform cadence instead of drifting phase.

Important constraints:

- This is high risk and multi-file. Do not treat it as a small DXGI waitable-swap-chain tweak.
- A waitable swap chain helps only if the app presents at display cadence; it does not phase-lock a
  24fps source-cadence loop by itself.
- Preserve the existing source-cadence path behind a feature flag until parity is proven.
- Do not change decoder selection, audio processing, BASS clock logic, normalization, tempo/pitch,
  WPF adapter boundaries, or unrelated renderer behavior as part of this work.
- 24p on 60Hz cannot become truly judder-free; the realistic goal is uniform 3:2 cadence unless the
  display path supports 24/48/120Hz presentation.

First step if this is ever approved: map the full composition, CDG, video-frame, coordinator, backend,
and vblank data flow before editing source.

## AV1 Hardware Decode Note

The AV1 software fallback observed with `D:\- Testing Files\23.98-stutter.mkv` is a GPU/driver
capability limitation, not a decoder-selection bug. The current code selects and attempts the AV1
hardware path, then falls back when the hardware path cannot produce frames.

Do not change decoder selection for this unless a future task explicitly targets codec capability
detection or user-visible hardware-decode reporting.

## Non-Goals

These are not roadmap items unless product goals change:

- VLC-style plugin/module architecture.
- Streaming/network playback.
- Disc/DVD/Blu-ray playback.
- General subtitle/SPU engine beyond CDG karaoke graphics.
- Cross-platform renderer abstraction beyond future adapter experiments.
- Full audio-filter framework beyond karaoke-focused volume, pitch, tempo, and normalization.
- Playlist/library ownership inside the media engine.
- Thread-count reduction without profiling evidence.
