# Project Analysis and Roadmap — FatzBombMedia.MediaControl — 2026-06-30

## 1. Executive Summary

`FatzBombMedia.MediaControl` is the UI-agnostic Core media engine: BASS audio, FFmpeg
video decode, indexed-color CDG karaoke graphics, and a D3D11 presentation stack with two
live backends (DirectComposition primary, native-HWND swap-chain fallback/secondary). The
architecture is clean and the layering rules (no WPF in Core; adapters own zero rendering)
are genuinely respected — `rg` found no `System.Windows.*`/`ImageSource`/`Dispatcher`/`D3DImage`
usage anywhere in Core source.

The engine is in good shape. There is **no significant dead code** — the three "Known Dead
Code" items in `AGENTS.md` have already been deleted, and most files flagged as suspect
(`AudioNormalizer`, `D3D11PresentationDiagnostics`, `BinaryCompatibility`) are fully live. The
two FramePresenter classes are a deliberate wrapper pair, not duplication.

The real opportunities are:

1. **Per-frame / per-update allocations** on the karaoke and composition hot paths — most
   notably a full ~64 KB CDG framebuffer snapshot allocated just to read a single `uint`
   wash color (`DxCdgEngine` → `DxCdgStateMachine.CreateSnapshot`), plus LINQ/iterator/record
   allocations every present in `D3D11MediaCompositionPresenter.PresentToTargets`.
2. **One blocking-async anti-pattern**: the operation-queue worker runs async load work via
   `operation().GetAwaiter().GetResult()`, tying up the single serialization thread (and
   violating the repo's own no-`.GetResult()` rule).
3. **Legacy naming drift**: a dangling `VorticeD3D11Renderer` doc-cref in ~6 files (the class
   no longer exists), `[VorticeD3D11…]` log tags across ~10 files, and the pervasive `DxCdg`
   prefix for a CDG path that no longer owns a dedicated DX presenter. None of this is a bug;
   it is maintainability debt and a future-rename candidate with public/NuGet API impact.
4. **A small number of reliability gaps**: a leaked `CancellationTokenSource` per CDG
   play/seek, missing `ConfigureAwait(false)` on four Core awaits, silent `catch {}` in the
   directory scanner, and no explicit device-removed (`DXGI_ERROR_DEVICE_REMOVED`) recovery
   branch at the present site.

Recommended first stage: a low-risk allocation-reduction pass (finding A1 alone is a one-line
fix on a steady-state karaoke path), kept strictly separate from naming and behavior changes.

## 2. Scope and Method

- **Target analyzed:** `FatzBombMedia.MediaControl` (Core engine). 121 `.cs` files, ~16,060 LOC.
- **Output file:** `ProjectAnalysisRoadmap [2026-06-30].md` (repo root).
- **Repo root:** `C:\Programming\FatzBombMedia` (resolved via `git rev-parse --show-toplevel`).
- **Graphify status:** **Used.** `graphify-out/graph.json` present (101 communities). Queried
  for the live render/present flow and used to seed which classes to inspect. Treated as
  supporting evidence only; all classifications were confirmed by direct source reads + `rg`.
- **Whole-repo search status:** **Used.** `rg` available
  (`...\WinGet\Links\rg.exe`). Searched all 3 projects plus `*.xaml/*.csproj/*.props/*.targets/
  *.sln/*.json/*.config/*.ps1/*.md` for every dead/legacy claim.
- **VLC access status:** **Accessible.** Fetched `videolan/vlc` `src/input/decoder.c` (engine
  core) for the comparison. Compared media-engine concerns only (decode threading, picture
  pools, clock/sync, backpressure) — not UI/app shell.
- **Major limitations:**
  - No tests or CI exist (`AGENTS.md`), so all findings are static-analysis based; runtime
    profiling was not performed. Allocation severities are reasoned from call frequency, not
    measured.
  - Native DLLs (BASS/FFmpeg) are not in the repo, so the engine was not executed.
  - VLC is C/LGPL with very different goals (general-purpose player vs. karaoke/trivia host);
    comparison is architectural reference only, not a portability assessment.

## 3. Overall Unbiased Assessment

### Pros

- Clean, enforced layering: Core has zero WPF/`System.Windows` leakage (verified by `rg` and a
  `scripts/check-wpf-boundary.ps1` guard).
- Modern, disciplined C#: `System.Threading.Lock`, no `async void` anywhere in Core, cancellation
  honored in decode/present/render loops, symmetric event subscribe/unsubscribe on most paths.
- Native resource disposal is generally correct, including on error paths (decoder cleanup,
  composition layer/cache/video-processor `catch` paths null + dispose textures/SRVs/RTVs,
  `PresentFrame` returns frames to a pool in `finally`).
- Sensible GPU design: indexed CDG resolved index→palette in an HLSL pixel shader rather than a
  CPU ARGB bitmap; layered composition (background/wash/overlay) with retained per-layer GPU
  resources and dirty-rect tracking.
- Fail-soft rendering: `result.Failure` paths log and `Reset()`/`ReleaseResources()` coherently.

### Cons

- Steady-state allocations on the karaoke/composition hot path (full-framebuffer snapshot to
  read one value; per-present LINQ/iterator/record churn).
- A blocking `GetAwaiter().GetResult()` inside the single operation-queue worker, contrary to
  the project's own async rules.
- Naming/documentation drift (`VorticeD3D11Renderer` ghost cref, `[VorticeD3D11…]` log tags,
  `DxCdg` prefix) that misrepresents the current design to a new reader.
- A few small reliability gaps (leaked CTS, missing `ConfigureAwait(false)`, silent scan catch,
  no explicit device-lost recovery branch).
- `AGENTS.md` "Known Dead Code" list is stale and now misleading.

### Neutral Observations

- Core is a packable NuGet (`GeneratePackageOnBuild`), so several `public` types unused in-repo
  (`IMediaEngine.SeekAsync`, `BinaryCompatibility` members) are plausibly intended API surface,
  not dead — they must be judged as API decisions, not deletions.
- The two-FramePresenter wrapper pair is a legitimate (if collapsible) layering choice.
- VLC parity is mostly not meaningful: FatzBombMedia targets a narrow karaoke/trivia use case
  with a CDG path VLC does not prioritize.

## 4. Current Live Architecture Summary

Single construction chain (all LIVE), from `Api/MediaControlPlayer.cs:36`:

```
MediaControlPlayer
 └─ NativeMediaPresentationCoordinator        (only IMediaRenderCoordinator impl)
     └─ NativeD3D11PresentationBridge          (Lock-guarded facade)
         ├─ D3D11DeviceResources               (device/context, video-processor)
         └─ D3D11RendererPresentationServices  (owns the whole present object graph)
             ├─ D3D11MediaCompositionPresenter  (layered compositor: bg/wash/CDG + HLSL)
             ├─ D3D11VideoProcessorPresenter    (VideoProcessorBlt for hardware frames)
             ├─ D3D11FramePresenter ←wrapped by D3D11PresentationFramePresenter
             ├─ D3D11NativeHwndSwapChainBackend (fallback primary + all secondary targets)
             ├─ D3D11DirectCompositionSingleTargetBackend (preferred primary)
             ├─ D3D11PresentationBackendSet / SurfaceController / BackendTarget / Snapshot
             ├─ D3D11FrameUploadResources / D3D11PresentationDiagnostics / PresentationState
             └─ D3D11PresentationResourceCleaner
```

- Pipeline orchestration: `Pipeline.PlaybackCoordinator` wires `AudioPipeline` + `CdgPipeline`
  + `VideoPipeline`; `PlaybackScheduler` drives decode/present threads; `PlaybackClock` /
  `AudioPresentationClock` provide timing; `ClockAudioEngine` supplies a clock-backed
  `IAudioEngine` for standalone-CDG and video-without-audio.
- CDG: `DxCdgEngine` (`ICdgEngine` + `IDxCdgSnapshotSource`) replays packets into
  `DxCdgStateMachine`, producing an immutable `DxCdgRenderSnapshot` (indices + palette + dirty).
- DI: `MediaPlayerServiceProvider.Create()` (singleton, no external overrides).

## 5. Current Hardware Rendering Pipeline Summary

Two live present paths, chosen by content:

- **Path A — hardware video, no CDG:** `IVideoEngine.FrameReady` →
  `NativeMediaPresentationCoordinator.OnVideoFrameReady` (`:495`) →
  `NativeD3D11PresentationBridge.PresentHardwareFrame` →
  `D3D11RendererPresentationServices.PresentHardwareFrame` (`:167`) →
  `D3D11PresentationFramePresenter` → `D3D11FramePresenter` →
  `D3D11VideoProcessorPresenter` (`VideoProcessorBlt`) → backend `Present`.
- **Path B — composition (karaoke):** background (BGRA/HW texture/retained) + wash + CDG overlay
  built into a `MediaCompositionFrame` →
  `D3D11RendererPresentationServices.PresentMediaCompositionVisible` (`:202`) →
  `D3D11MediaCompositionPresenter.PresentToTargets` (`:221`) draws layers to each target RTV;
  the CDG snapshot is uploaded as two GPU textures (index + palette) and resolved in HLSL
  `PSMain` (`D3D11MediaCompositionPresenter.cs:1175`), with the background index transparent.

Backend selection: DirectComposition is attempted first and preferred
(`NativeMediaPresentationCoordinator.cs:322-324`, `D3D11PresentationBackendSet.Active :38`);
native-HWND swap-chain is the fallback for the primary surface and **always** used for
secondary/fan-out targets.

Layer model (`MediaCompositionLayerId`): `Background = 0`, `Wash = 1`, `Overlay = 2`,
emitted in draw order by `MediaCompositionFrame.Layers.cs:12`.

## 6. Performance Findings

### Finding: 64 KB snapshot allocated to read one `uint` (wash color)

- **Area:** CDG / karaoke steady-state path
- **Files:** `Core/DxCdg/DxCdgEngine.cs:236-246` (callers `:45`, `:62`, `:193`);
  `Core/DxCdg/DxCdgStateMachine.cs:145-169`
- **Symbols:** `DxCdgEngine.RefreshBackgroundWashUnsafe`, `DxCdgStateMachine.CreateSnapshot`
- **Risk:** Medium (perf, GC pressure; behavior unchanged)
- **Category:** Performance
- **Evidence:** `RefreshBackgroundWashUnsafe` calls `CreateSnapshot(consumeDirtyRegions:false)`,
  which does `_colorIndices.ToArray()` (300×216 = 64,800 bytes) + `_paletteArgb.ToArray()` +
  dirty-rect copy (confirmed `DxCdgStateMachine.cs:149-150`), yet only reads
  `snapshot.BackgroundWashArgb`. Invoked on lyric changes and every opacity/override setter.
- **Why it matters:** Churns ~64 KB per lyric update during karaoke playback → steady Gen0 GC.
- **Recommendation:** Read the already-exposed cached value `_dxCdgState.BackgroundWashArgb`
  (`DxCdgStateMachine.cs:38`) instead of building a snapshot.
- **Suggested action:** Refactor
- **Stage candidate:** Stage 1

### Finding: Per-present LINQ / List / iterator / record allocations in compositor

- **Area:** Composition present path (display-refresh cadence)
- **Files:** `Core/Graphics/Composition/D3D11MediaCompositionPresenter.cs:240-253, 297, 346, 478`;
  `Core/Graphics/Composition/D3D11/D3D11CompositionLayerCache.cs:35, 43, 61-65, 80-84`;
  `Core/Graphics/Composition/MediaCompositionFrame.Layers.cs:12`
- **Symbols:** `PresentToTargets`, `EnumerateLayers` (yield iterator), `MediaCompositionLayer`
  (`sealed record`), `D3D11CompositionLayerCache.Capture/CreateUpdatePlan/MarkPlanConsumed`
- **Risk:** Medium (perf)
- **Category:** Performance
- **Evidence:** `Where(...).ToList()` (`:240`, `:251`), `Select(...)` (`:249`),
  `new List<...>` (`:297`), `EnumerateLayers().FirstOrDefault(...)` (`:346`, `:478`) each
  allocate an enumerator plus `new MediaCompositionLayer` records; `Capture` re-enumerates +
  `_layers.Keys.ToArray()` (`:43`); plan build runs `Where/OrderBy/Select/ToArray` per present.
- **Why it matters:** A dozen-plus short-lived objects allocated per composited frame.
- **Recommendation:** Cache the (≤3) layer descriptors, look up by layer id instead of
  `FirstOrDefault` over a fresh iterator, replace LINQ with reusable scratch lists/index loops.
- **Suggested action:** Refactor
- **Stage candidate:** Stage 1

### Finding: `CreateSnapshot` copies the full index buffer per legitimate snapshot

- **Area:** CDG → GPU upload
- **Files:** `Core/DxCdg/DxCdgStateMachine.cs:145-169`;
  `Core/Graphics/NativeMediaPresentationCoordinator.cs` (`TryCreateAndCacheDxCdgSnapshot :793`)
- **Symbols:** `DxCdgStateMachine.CreateSnapshot`
- **Risk:** Low-Medium (perf)
- **Category:** Performance
- **Evidence:** `_colorIndices.ToArray()` (64,800 bytes) + `_paletteArgb.ToArray()` each real
  snapshot; retained `_lastDxCdgSnapshot` keeps one alive.
- **Why it matters:** Repeated 64 KB allocations during CDG presentation.
- **Recommendation:** Pool index/palette buffers (`ArrayPool<byte>`) keyed by revision, or pass
  dirty regions only when `!FullFrame`.
- **Suggested action:** Refactor
- **Stage candidate:** Stage 1 (or 2 if pooling complexity warrants isolation)

### Finding: Array allocation per seek-decode attempt

- **Area:** Seek / scrubbing
- **Files:** `Core/Video/VideoDecoder.cs:149`; `Pipeline/VideoPipeline.cs:1314-1336`
- **Symbols:** `VideoDecoder.GetQueuedFramesSnapshot` (`=> [.. _frameQueue]`),
  `VideoPipeline.HasAcceptableSeekFrame`/`DecodeForSeekTarget`
- **Risk:** Low-Medium (perf, scrubbing only)
- **Category:** Performance
- **Evidence:** New array snapshot of the `ConcurrentQueue` per decode attempt; loop runs up to
  32–72 attempts during scrub.
- **Why it matters:** Allocation storm during slider scrubbing.
- **Recommendation:** Enumerate the queue directly without materializing an array, or peek tail.
- **Suggested action:** Refactor
- **Stage candidate:** Stage 1

### Finding: `VideoProcessorStream[]` + `Marshal.SizeOf` per frame/draw

- **Area:** Hardware video blit / compositor draw
- **Files:** `Core/Graphics/D3D/Video/D3D11VideoProcessorPresenter.cs:115-124`;
  `Core/Graphics/Composition/D3D11MediaCompositionPresenter.cs:801`;
  `Pipeline/VideoPipeline.cs:1690-1704`
- **Symbols:** `BlitTextureToPresentation`, `SetQuadConstants`, `PresentFrame`
- **Risk:** Low (perf)
- **Category:** Performance
- **Evidence:** `VideoProcessorStream[] streams = [ new ... ]` per blit; `Marshal.SizeOf<T>()`
  recomputed per layer per present; `new VideoFrameEventArgs` per presented frame.
- **Why it matters:** One small allocation per frame on each fast path.
- **Recommendation:** Reuse a single-element `VideoProcessorStream[]` field; cache
  `Unsafe.SizeOf<T>()`/`const`; reuse a mutable args instance on the single presentation thread
  (only if host EventArgs semantics allow).
- **Suggested action:** Refactor
- **Stage candidate:** Stage 1

## 7. Memory and Lifetime Findings

### Finding: Completion-monitor `CancellationTokenSource` never disposed

- **Area:** Standalone CDG playback/seek
- **Files:** `Pipeline/CdgPipeline.cs:336` (create), `:341-357` (`StopCompletionMonitor`)
- **Symbols:** `_completionCts`, `StartCompletionMonitor`, `StopCompletionMonitor`
- **Risk:** Medium (resource leak)
- **Category:** Memory / Lifetime
- **Evidence:** `StopCompletionMonitor` nulls and `Cancel()`s the CTS but never calls
  `Dispose()`; a new CTS is allocated on every standalone CDG `Play()` and on `SeekCore` while
  playing.
- **Why it matters:** Each play/seek leaks a `CancellationTokenSource` (and any lazily-created
  `WaitHandle`) for the session lifetime.
- **Recommendation:** `Dispose()` the CTS after `Cancel()` (the monitor task already swallows
  `OperationCanceledException`).
- **Suggested action:** Refactor
- **Stage candidate:** Stage 3

### Finding: Implicit COM-refcount assumption on `ImmediateContext`

- **Area:** Native device adoption
- **Files:** `Core/Graphics/D3D/Device/D3D11DeviceResources.cs:92-95` (assign), `:147` (dispose)
- **Symbols:** `InitializeFromNativeDevice`, `Cleanup`
- **Risk:** Low (verify)
- **Category:** Memory / Lifetime
- **Evidence:** `Context = Device.ImmediateContext` then `Cleanup` disposes `Context`; relies on
  Vortice's `ImmediateContext` getter AddRef'ing to balance the Dispose.
- **Why it matters:** Correct under current Vortice, but an implicit refcount assumption that
  could regress on upgrade.
- **Recommendation:** Add a clarifying comment; confirm against Vortice version notes.
- **Suggested action:** Investigate
- **Stage candidate:** Stage 0 (note only)

> Positive: decoder cleanup (`VideoDecoder.Close :828-881`, hardware-frame release `:925-1005`),
> composition layer/cache/video-processor disposal on `catch` paths, and `PresentFrame`'s
> `finally` frame-return were reviewed and are sound.

## 8. Reliability and Error-Handling Findings

### Finding: Silent `catch {}` in directory scanner

- **Area:** File scanning
- **Files:** `Core/Files/DirectoryScanner.cs:105, 116, 135`
- **Risk:** Medium (diagnosability)
- **Category:** Reliability
- **Evidence:** Three empty `catch { }` around enumeration/access; failures dropped with no log.
- **Why it matters:** Skipping inaccessible dirs is reasonable, but total silence makes "missing
  files" undiagnosable.
- **Recommendation:** Log at verbose level (path + exception type) before continuing.
- **Suggested action:** Refactor
- **Stage candidate:** Stage 3

### Finding: No explicit device-removed recovery at present site

- **Area:** Swap-chain present
- **Files:** `Core/Graphics/D3D/SwapChain/D3D11SwapChainPresenter.cs:43-50`
- **Risk:** Low-Medium
- **Category:** Reliability
- **Evidence:** `if (result.Failure) Debug.WriteLine(...)` — `DXGI_ERROR_DEVICE_REMOVED/RESET`
  is not distinguished from transient failures.
- **Why it matters:** A removed/reset device (driver update, GPU reset) would spam logs with no
  re-initialize path, unless a higher layer handles it.
- **Recommendation:** Detect device-removed HRESULTs and signal a re-initialize path; first
  confirm whether any caller already handles device-lost.
- **Suggested action:** Investigate
- **Stage candidate:** Stage 4

### Finding: Silent teardown catches around event unsubscribe

- **Area:** Attach/detach
- **Files:** `Core/Graphics/NativeMediaPresentationCoordinator.cs:368-370, 394-396, 1004-1006,
  1018-1020`
- **Risk:** Low
- **Category:** Reliability
- **Evidence:** Empty `catch {}` around `FrameReady -=` / `FrameInvalidated -=`.
- **Why it matters:** Acceptable during teardown, but recurring failures would be invisible.
- **Recommendation:** Log under `EnableLogging`.
- **Suggested action:** Keep (optionally log)
- **Stage candidate:** Stage 3

## 9. Blocking, Threading, and Async Findings

### Finding: Operation-queue worker blocks on async via `GetAwaiter().GetResult()`

- **Area:** Load serialization
- **Files:** `Core/Threading/MediaControlOperationQueue.cs:52-57`; routed from
  `Api/MediaControlPlayer.cs:122-134`; blocking continuation `Controllers/MediaFile.cs:132, 181`
- **Symbols:** `MediaControlOperationQueue.Enqueue<T>(Func<Task<T>>)`, `LoadAsync`
- **Risk:** Medium-High (threading; violates repo async rule)
- **Category:** Threading
- **Evidence:** `Enqueue(() => operation().GetAwaiter().GetResult())` (confirmed
  `MediaControlOperationQueue.cs:56`). `LoadAsync` passes a `Task<bool>` lambda, so the single
  worker thread (`Run :70-84`) synchronously blocks for the whole async load — including
  `MediaFile.WaitForQuickNormalizationAsync` which polls `Task.Delay(50)` up to 2 s.
- **Why it matters:** The one serialization thread is tied up for the entire load; any awaited
  continuation that itself needs the queue could deadlock; throughput serializes behind blocking
  waits. Directly contradicts `AGENTS.md` ("no blocking async with `.Result`/`.Wait()`").
- **Recommendation:** Make the worker pump async (await the operation on the worker loop) or
  schedule `Func<Task<T>>` work without a sync block.
- **Suggested action:** Refactor
- **Stage candidate:** Stage 5 (isolated; touches load path — verify carefully)

### Finding: Missing `ConfigureAwait(false)` in four Core awaits

- **Area:** Audio normalization async
- **Files:** `Core/Audio/AudioEngine.cs:403, 410`; `Core/Audio/AudioNormalizer.cs:64, 77`
- **Risk:** Medium
- **Category:** Threading
- **Evidence:** e.g. `return await _normalizer.AnalyzePeakQuickAsync(...)` without
  `.ConfigureAwait(false)`; the internal driver (`AudioEngine.cs:554/558`) and `CdgPipeline.cs:371`
  do it correctly, so these four are inconsistent.
- **Why it matters:** `AGENTS.md` mandates `.ConfigureAwait(false)` in Core pipeline async; a
  host with a `SynchronizationContext` would marshal continuations back unnecessarily.
- **Recommendation:** Add `.ConfigureAwait(false)` to all four awaits.
- **Suggested action:** Refactor
- **Stage candidate:** Stage 3

### Finding: Shared mutable state read under inconsistent/absent locks

- **Area:** Video pipeline
- **Files:** `Pipeline/VideoPipeline.cs:104-106, 168, 671, 1296-1297, 1680-1716`
- **Symbols:** `_videoDecoder` (read unlocked vs nulled under `_decodeLock`), `_currentFrame`
  (guarded by `_presentationLock` in `PresentFrame` but read under `_decodeLock` in
  `IsTinyVideoSeek`)
- **Risk:** Low (currently benign; latent)
- **Category:** Threading
- **Evidence:** Reference reads are atomic and null-guarded; `_currentFrame` is currently
  serialized via `_transportGate`, so two different locks protecting one field is benign today.
- **Why it matters:** Latent inconsistency that could surface under future refactors.
- **Recommendation:** Standardize/document the lock guarding `_currentFrame`; keep capturing
  `_videoDecoder` into a local (already done in loop bodies).
- **Suggested action:** Investigate
- **Stage candidate:** Stage 4

> Positive: no `async void` in Core; no lock held across `await`; fire-and-forget tasks
> (`AudioEngine.RunNormalizationAnalysisAsync :549-568`, `CdgPipeline.MonitorCompletionAsync
> :359-377`) are try/catch-wrapped; `PlaybackScheduler.StopAndWait`'s `task.Wait(2000)` is
> intentional/documented.

## 10. Dead, Legacy, or Misplaced Code Findings

### Finding: `IMediaEngine.SeekAsync` + impl — no callers

- **Area:** Engine abstraction
- **Files:** `Core/Abstractions/IMediaEngine.cs:52`; `Pipeline/PipelineMediaEngine.cs:202`
- **Symbols:** `IMediaEngine.SeekAsync`, `PipelineMediaEngine.SeekAsync`
- **Risk:** Low
- **Category:** Dead Code (public API, unused in-repo)
- **Evidence:** `rg "\.SeekAsync\("` whole-repo → 0 call sites; public surface exposes only sync
  `Seek`. This is the migrated descendant of the AGENTS.md-noted dead `SeekAsync`.
- **Why it matters:** Carries an unused async wrapper on a `public` engine interface.
- **Recommendation:** Remove both, or formally bless as intended public engine API.
- **Suggested action:** Remove (or Keep as documented API)
- **Stage candidate:** Stage 2

### Finding: `CompatibilityInfo.IsMatch` / `.CurrentProcess` / `.FileCpu` — computed, never read

- **Area:** Binary compatibility
- **Files:** `Core/Files/BinaryCompatibility.cs:28-30`; consumer `Core/Files/DirectoryScanner.cs:196`
- **Symbols:** `CompatibilityInfo.IsMatch/CurrentProcess/FileCpu`
- **Risk:** Low
- **Category:** Dead Code (dead reads) / public API unused in-repo
- **Evidence:** Whole-repo reads outside the file: `.IsMatch` 0, `.CurrentProcess` 0, `.FileCpu`
  0; only `.IsCompatible` is consumed (`DirectoryScanner.cs:196`).
- **Why it matters:** Rich CPU-type model feeds an unused `match` computation.
- **Recommendation:** Either collapse `Analyze` to return just compatibility, or keep as
  deliberate diagnostic API (then mark unused-in-repo, consider `internal`).
- **Suggested action:** Investigate / Refactor
- **Stage candidate:** Stage 2

### Finding: `AGENTS.md` "Known Dead Code" list is stale

- **Area:** Documentation
- **Files:** `AGENTS.md:48-51`; `docs/CODECLEANUP.md`
- **Risk:** Low (misleading guidance)
- **Category:** Legacy (doc drift)
- **Evidence:** All three listed items are already deleted — no `MediaControlSession.cs`, no
  `MediaControlPlayer.Create`, no `MediaControlPlayer.SeekAsync` (`rg` → 0 hits each).
- **Why it matters:** Actively misleads future cleanup work.
- **Recommendation:** Update/remove the "Known Dead Code" section.
- **Suggested action:** Remove (doc)
- **Stage candidate:** Stage 0

### Misplaced code

No layer violations found in Core. No `System.Windows.*`/`ImageSource`/`Dispatcher`/`D3DImage`
in Core source (only in the WPF-boundary guard script and `README.md`/`Comparison.md` text).
Audio normalization living in `Core/Audio` is appropriate engine-layer placement.

### Cleared suspects (NOT dead — confirmed live)

`AudioNormalizer.cs`, `D3D11PresentationDiagnostics.cs`, `BinaryCompatibility.cs` (type),
both FramePresenters, all five `*Presenter` classes, `VideoAudioEngine`, `ClockAudioEngine`,
`RenderPipeline`, `MediaSession`, `AudioPresentationClock`, `PrecisionWaiter`,
`MediaControlOperationQueue`, `NativeD3D11PresentationBridge`, `FileTree`, `DirectoryScanner`,
`MediaBackgroundResolver/Candidate` — each has ≥1 verified external construction/call site.

## 11. Complete File / Self-Contained Dead-Code Findings

- **No fully-dead files found** in Core. The only file-level candidate is documentation:
  `Comparison.md` (repo root) references removed classes (`OptimizedGraphicsRenderer.cs`,
  `D3D9InteropSurface.cs`, `VorticeD3D11WpfImagePresenter.cs`, `WpfD3DImageSurfaceSizingMode`)
  that do not exist in the current tree → stale design doc.
- `D3D11PresentationBackendKind` enum has a clean gap at value `1` (removed legacy
  `D3DImage`→`UiSharedSurface` WPF shared-surface backend, deleted in commit `d57f8c1`). `rg`
  found no stale references to the removed value — gap is safe, documented here for history.

## 12. Naming and Legacy-Name Findings

### Finding: `VorticeD3D11Renderer` doc-cref to a non-existent class

- **Area:** XML doc comments
- **Files:** `D3D11FramePresenter.cs:13`, `D3D11PresentationFramePresenter.cs:13`,
  `D3D11PresentationState.cs:9`, `D3D11RendererPresentationServices.cs:16`,
  `ID3D11PresentationBackend.cs:8`, `D3D11DirectCompositionSingleTargetBackend.cs:18`
- **Risk:** Low (Medium if `GenerateDocumentationFile` is ever enabled → CS1574 with
  `TreatWarningsAsErrors`)
- **Category:** Naming / Legacy
- **Evidence:** `rg "class \w*Renderer"` → only `D3D11RendererPresentationServices`; no
  `VorticeD3D11Renderer` exists. Builds only because no doc-file generation validates crefs.
- **Recommendation:** Repoint crefs to the actual coordinating type(s) or remove them.
- **Suggested action:** Rename
- **Stage candidate:** Stage 6

### Finding: `[VorticeD3D11…]` legacy log prefixes

- **Area:** Debug logging (~10 files)
- **Files:** `D3D11DeviceResources.cs`, `D3D11VideoProcessorPresenter.cs`,
  `D3D11PresentationDiagnostics.cs`, `D3D11SwapChainFactory.cs`, `D3D11SwapChainPresenter.cs`,
  `D3D11PresentationBackendSet.cs`, `D3D11PresentationSurfaceController.cs`, both backends
- **Risk:** Low (cosmetic / log-grepping)
- **Category:** Naming / Legacy
- **Evidence:** Dozens of `Debug.WriteLine("[VorticeD3D11]...")`; Vortice is just the interop
  binding, not a renderer by that name.
- **Recommendation:** Rename prefix to a stable tag (e.g. `[D3D11]`).
- **Suggested action:** Rename (batch later)
- **Stage candidate:** Stage 6

### Finding: `DxCdg` prefix — no dedicated DX CDG presenter remains

- **Area:** CDG subsystem (`Core/DxCdg/*` + ~65 references)
- **Files:** entire `Core/DxCdg/` folder; `RenderPipeline.cs`,
  `NativeMediaPresentationCoordinator.cs`, `D3D11MediaCompositionPresenter.cs`,
  `D3D11CompositionLayerState.cs`, `MediaPlayerServiceProvider.cs:64`
- **Symbols:** `DxCdgEngine`, `IDxCdgSnapshotSource`, `DxCdgStateMachine`, `DxCdgRenderSnapshot`,
  `DxCdgColorManager`, `DxCdgPacket`, `DxCdgDirtyRect`, `DxCdgConstants`, `DxCdgEngineState`
- **Risk:** Medium (`DxCdgEngine`/`IDxCdgSnapshotSource` are `public` + DI-registered → rename is
  a binary API change for the packable NuGet)
- **Category:** Naming / Legacy
- **Evidence:** The CDG snapshot is consumed by the **shared** `D3D11MediaCompositionPresenter`;
  there is no separate "DX CDG" presenter. Public abstraction is plain `ICdgEngine`; the `Dx`
  prefix is internal history.
- **Why it matters:** Misrepresents the current design; the `Dx` prefix implies a separate DX
  path that does not exist.
- **Recommendation:** Plan a dedicated `DxCdg`→`Cdg` rename pass with full public/NuGet API
  awareness; do not bundle with feature or behavior work.
- **Suggested action:** Rename candidate
- **Stage candidate:** Stage 7 (isolated, high-care)

### Public-visibility tightening (minor)

`DirectoryScanner.CleanList` (`:189`) and `MatchesCompatibility` (`:191`) are `public` but only
self-called → could be `private`/`internal`. `BinaryCompatibility`/`CpuType`/`FileCpuType`/
`CompatibilityInfo` are `public` but consumed only inside Core → `internal` unless intended
packable diagnostic API. (`LocateFiles`/`LocateFilesDirectory` ARE used externally by
`Bootstrap.LibraryLoader.cs:39,134` — keep public.)

## 13. Renderer and Presentation Pipeline Findings

- **Live path is well-defined and singular** (Section 5). No redundant/competing renderer was
  found; the two-FramePresenter pair is a thin wrapper chain
  (`D3D11PresentationFramePresenter` → `D3D11FramePresenter` → `D3D11VideoProcessorPresenter`),
  not duplication.
- **Collapsible seam (optional):** `D3D11FramePresenter` ↔ `D3D11PresentationFramePresenter`
  could be merged (neither has callers outside `D3D11RendererPresentationServices`). Low value,
  touches the hardware-frame path — isolate and verify if pursued.
- **Legacy presentation scaffolding is already gone:** the WPF `D3DImage`/shared-surface backend
  (enum value 1) was removed; only the enum gap and stale doc comments remain.
- **Hot-path allocations are the main renderer concern** — see Section 6 (A2/A3/A5).
- **Device-lost handling** is the main renderer reliability gap — see Section 8.

## 14. VLC Engine Comparison

Reference: `videolan/vlc` `src/input/decoder.c` (engine core; UI/app shell excluded). VLC is a
general-purpose, format-agnostic C engine; FatzBombMedia is a focused karaoke/trivia engine with
a first-class CDG path. Most differences reflect divergent goals.

### FatzBombMedia Better for This Project

- **First-class CDG/karaoke pipeline** (indexed state machine → GPU index+palette shader, wash
  layer, retained composition). VLC treats CDG as a niche codec with no comparable wash/overlay
  composition model.
- **Tight WPF-adapter boundary + packable Core** with enforced no-UI-in-Core rule — cleaner for
  embedding in a .NET desktop host than VLC's plugin/object framework.
- **Modern managed safety**: `System.Threading.Lock`, structured disposal, cancellation tokens —
  fewer manual refcount/lifetime hazards than VLC's hand-rolled `vlc_object`/refcounting.

### VLC Stronger

- **Picture pools everywhere** (`picture_pool_NewFromFormat`, `picture_pool_Wait`,
  `ModuleThread_NewVideoBuffer`) — VLC recycles frame buffers rather than allocating. This
  directly contrasts with FatzBombMedia's per-snapshot 64 KB allocations (Section 6) and is the
  clearest borrowable idea: **pool the CDG index/palette buffers and frame args.**
- **Dedicated clock abstraction** (`vlc_clock_ConvertToSystem`, lock/start/convert) with a
  formal master/slave A/V sync model and explicit preroll/drift handling — more rigorous than a
  single audio-presentation clock for hard A/V sync edge cases.
- **Backpressure via a bounded fifo** with documented locking rules and decoder pacing — a
  mature template if FatzBombMedia ever needs stronger decode/present flow control.
- **Format/vout reconfiguration** (chroma/SAR/orientation/HDR mastering change detection) is more
  comprehensive than the current fixed-format assumptions.

### Near Equal / Project-Specific Differences

- **Thread-per-decoder vs. scheduler threads**: both isolate decode from present; comparable for
  this workload.
- **Hardware decode + GPU present**: both use GPU video processing; FatzBombMedia's
  VideoProcessorBlt path is conceptually similar to VLC's vout/decoder-device flow.
- **Subtitles/SPU, stream-output, CC**: VLC has them; not in scope for FatzBombMedia's goals →
  comparison not meaningful.

### Improvement Suggestions from Comparison

1. **Adopt buffer pooling** (VLC's strongest idea) for CDG snapshots and frame args
   (`ArrayPool<byte>` / reusable structs) — addresses Section 6 directly. Severity: Medium.
2. **Consider a small explicit clock abstraction** only if hard A/V-sync edge cases appear;
   otherwise the current clock is adequate for karaoke. Severity: Low.
3. **Do NOT** import VLC's plugin/object framework, fifo-locking complexity, or general-codec
   breadth — it would be overengineering against FatzBombMedia's focused goals.

## 15. Risk Matrix

| # | Finding | Category | Severity | Action |
|---|---------|----------|----------|--------|
| A1 | 64 KB snapshot to read one `uint` wash | Performance | Medium | Refactor |
| C1 | Op-queue worker blocks on async (`GetResult`) | Threading | Med-High | Refactor |
| A2 | Per-present LINQ/iterator/record allocations | Performance | Medium | Refactor |
| A3 | `CreateSnapshot` full-buffer copy per snapshot | Performance | Low-Med | Refactor |
| B1 | Completion-monitor CTS never disposed | Memory | Medium | Refactor |
| C2 | Missing `ConfigureAwait(false)` ×4 | Threading | Medium | Refactor |
| D1 | Silent `catch {}` in DirectoryScanner | Reliability | Medium | Refactor |
| A4 | Array alloc per seek-decode attempt | Performance | Low-Med | Refactor |
| A5 | Per-frame `VideoProcessorStream[]`/`Marshal.SizeOf` | Performance | Low | Refactor |
| D2 | No explicit device-removed recovery | Reliability | Low-Med | Investigate |
| N1 | `VorticeD3D11Renderer` ghost doc-cref | Naming | Low | Rename |
| N2 | `[VorticeD3D11…]` log prefixes | Naming | Low | Rename |
| N3 | `DxCdg` prefix (public API) | Naming | Medium | Rename (isolated) |
| DC1 | `IMediaEngine.SeekAsync` unused | Dead Code | Low | Remove/Keep |
| DC2 | `CompatibilityInfo` unread members | Dead Code | Low | Investigate |
| DOC1 | `AGENTS.md` stale "Known Dead Code" | Legacy | Low | Remove (doc) |
| DOC2 | `Comparison.md` references removed classes | Legacy | Low | Remove (doc) |

## 16. Recommended Smoke Tests and Validation

No automated tests exist; use `docs/VALIDATION.md` (15-item checklist) plus the
`FatzBombMedia.Player` host. After any stage:

1. `dotnet build FatzBombMedia.sln --no-restore` (zero warnings).
2. `dotnet build -c Release FatzBombMedia.sln --no-restore` (warnings-as-errors).
3. WPF-boundary check: `scripts/check-wpf-boundary.ps1` (Core must stay UI-free).
4. Manual host smoke (requires x64 BASS + FFmpeg DLLs in `FatzBombMedia.Player/native/`):
   - Audio-only playback (volume/pitch/tempo, normalization on/off).
   - Standalone CDG + audio+CDG karaoke (wash color change, lyric updates, seek/scrub).
   - Video with audio (hardware path) + video-without-audio (clock path).
   - Seek/scrub responsiveness; secondary/fullscreen display fan-out.
   - DirectComposition vs. native-HWND fallback (force fallback to verify both backends).

## 17. Open Questions / Human Confirmation Needed

1. Is `IMediaEngine.SeekAsync` intended public API for external consumers, or removable?
2. Is `BinaryCompatibility` meant as a packable diagnostic API, or should it be `internal`?
3. Is the `DxCdg`→`Cdg` public rename acceptable as a (binary-breaking) NuGet API change, and is
   now the right time?
4. Does any layer already handle `DXGI_ERROR_DEVICE_REMOVED` (driver update/GPU reset), or is a
   re-initialize path genuinely missing?
5. Is the operation-queue load path ever re-entrant (could a queued op await something that needs
   the queue)? Confirms the deadlock risk severity for C1.

## 18. Staged Improvement Roadmap

### Roadmap Principles

- One concern per stage; build after each stage.
- Separate dead-code removal, performance, naming, and behavior into distinct stages.
- Keep renderer/present-path changes isolated and high-care.
- Stop and report if any whole-repo guard reveals unexpected live usage.
- Update docs (`CODECLEANUP.md`, `AGENTS.md`, `CURRENT_ARCHITECTURE.md`) after relevant stages.

### Stage 0 — Baseline and Guards (docs only)

- **Goal:** Establish a clean baseline; fix misleading docs without touching source.
- **Risk:** Low
- **Scope:** Update `AGENTS.md` "Known Dead Code" (all items already gone); mark/trim
  `Comparison.md` as historical; note the `ImmediateContext` refcount assumption.
- **Do not touch:** Any `.cs`/`.xaml`/`.csproj`.
- **Likely files affected:** `AGENTS.md`, `Comparison.md`, `docs/CODECLEANUP.md`.
- **Required whole-repo guards:** Re-confirm `rg` 0-hits for the three legacy symbols.
- **Graphify usage:** Confirm current live coordinator chain.
- **Build commands:** `dotnet build FatzBombMedia.sln --no-restore`.
- **Smoke tests:** None (docs only).
- **Stop conditions:** Any of the "already deleted" symbols actually still present.
- **Exit criteria:** Docs reflect reality; baseline build green.

### Stage 1 — Hot-Path Allocation Reduction (no behavior change)

- **Goal:** Remove steady-state allocations on karaoke/composition/seek paths.
- **Risk:** Medium (perf-sensitive code; behavior must be identical)
- **Scope:** A1 (read cached `BackgroundWashArgb`), A2 (cache layer descriptors, id lookup,
  reusable scratch lists), A3 (pool index/palette buffers), A4 (enumerate queue without array),
  A5 (reusable `VideoProcessorStream[]`, cached size, frame-args reuse).
- **Do not touch:** Backend selection, present ordering, shader logic, public API.
- **Likely files affected:** `DxCdgEngine.cs`, `DxCdgStateMachine.cs`,
  `D3D11MediaCompositionPresenter.cs`, `D3D11CompositionLayerCache.cs`,
  `MediaCompositionFrame.Layers.cs`, `VideoDecoder.cs`, `VideoPipeline.cs`,
  `D3D11VideoProcessorPresenter.cs`.
- **Required whole-repo guards:** Verify `CreateSnapshot`/`EnumerateLayers`/
  `GetQueuedFramesSnapshot` callers before changing semantics.
- **Graphify usage:** Confirm no off-path callers of the changed methods.
- **Build commands:** `dotnet build FatzBombMedia.sln --no-restore` then
  `dotnet build -c Release FatzBombMedia.sln --no-restore`.
- **Smoke tests:** Full karaoke (wash change, lyric updates, seek/scrub); video hardware path;
  visual parity vs. baseline.
- **Stop conditions:** Any visual diff, dirty-rect regression, or pooled-buffer aliasing.
- **Exit criteria:** Allocations removed; identical output; Release build clean.

### Stage 2 — Dead-Code / API-Surface Cleanup

- **Goal:** Remove or formally bless unused API.
- **Risk:** Low-Medium (public/NuGet surface)
- **Scope:** DC1 (`IMediaEngine.SeekAsync` remove or document), DC2 (`CompatibilityInfo`
  unread members; consider `internal` for `BinaryCompatibility`/`DirectoryScanner` self-only
  members).
- **Do not touch:** Renderer, threading, naming.
- **Likely files affected:** `IMediaEngine.cs`, `PipelineMediaEngine.cs`,
  `BinaryCompatibility.cs`, `DirectoryScanner.cs`.
- **Required whole-repo guards:** `rg` across all projects + reflection/DI/XAML before removal.
- **Graphify usage:** Confirm no callers.
- **Build commands:** Restore + Debug + Release builds.
- **Smoke tests:** File scan + load still locate media; compatibility filtering unchanged.
- **Stop conditions:** Any external/reflection reference appears.
- **Exit criteria:** Surface reduced or explicitly documented; builds green.

### Stage 3 — Reliability and Async Correctness (small, safe)

- **Goal:** Fix leak + async/diagnostic gaps.
- **Risk:** Low
- **Scope:** B1 (dispose completion CTS), C2 (add `ConfigureAwait(false)` ×4), D1 (verbose-log
  the scanner catches), optional D-teardown logging.
- **Do not touch:** Render path, op-queue, naming.
- **Likely files affected:** `CdgPipeline.cs`, `AudioEngine.cs`, `AudioNormalizer.cs`,
  `DirectoryScanner.cs`, `NativeMediaPresentationCoordinator.cs` (logging only).
- **Required whole-repo guards:** Confirm CTS lifetime; verify no caller depends on context
  capture.
- **Graphify usage:** None required.
- **Build commands:** Restore + Debug + Release.
- **Smoke tests:** Repeated CDG play/seek (no handle growth); normalization async; scan errors
  now logged.
- **Stop conditions:** Disposed-CTS use; behavior change in normalization.
- **Exit criteria:** Leak fixed; awaits consistent; scanner observable.

### Stage 4 — Threading Hardening (investigate-first)

- **Goal:** Resolve latent lock inconsistency and device-lost handling.
- **Risk:** Medium (concurrency + renderer)
- **Scope:** C3 (standardize `_currentFrame` lock; document `_videoDecoder` capture), D2 (decide
  and, if missing, add device-removed re-init signal).
- **Do not touch:** Allocation work, naming.
- **Likely files affected:** `VideoPipeline.cs`, `D3D11SwapChainPresenter.cs`, possibly
  `D3D11PresentationResourceCleaner.cs`/services.
- **Required whole-repo guards:** Map all `_currentFrame`/`_videoDecoder` access; trace
  device-lost handling up the call chain first.
- **Graphify usage:** Trace present → backend → device for recovery path.
- **Build commands:** Restore + Debug + Release.
- **Smoke tests:** Seek stress; forced device reset (driver restart) if feasible.
- **Stop conditions:** Any new race or present stall.
- **Exit criteria:** Single documented lock per field; defined device-lost behavior.

### Stage 5 — Operation-Queue Async Pump (isolated)

- **Goal:** Remove `GetAwaiter().GetResult()` blocking on the worker (C1).
- **Risk:** Medium-High (load path; serialization semantics)
- **Scope:** Make the worker await async operations without blocking, preserving ordering.
- **Do not touch:** Anything outside the queue + its direct callers.
- **Likely files affected:** `MediaControlOperationQueue.cs`, `MediaControlPlayer.cs`
  (load routing), `MediaFile.cs` (verify wait interaction).
- **Required whole-repo guards:** Enumerate every `Enqueue`/`LoadAsync` caller; confirm
  re-entrancy assumptions (Open Question 5).
- **Graphify usage:** Map load → queue → normalization wait.
- **Build commands:** Restore + Debug + Release.
- **Smoke tests:** Rapid load/cancel/reload; concurrent loads; verify ordering + no deadlock.
- **Stop conditions:** Any ordering change, deadlock, or load regression.
- **Exit criteria:** No blocking on worker; identical ordering; load stable.

### Stage 6 — Cosmetic Naming Cleanup (low-risk strings/docs)

- **Goal:** Remove legacy renderer naming that misrepresents the design.
- **Risk:** Low
- **Scope:** N1 (fix/remove `VorticeD3D11Renderer` crefs), N2 (rename `[VorticeD3D11…]` log
  prefixes to `[D3D11]`).
- **Do not touch:** Type names, public API, behavior.
- **Likely files affected:** ~10 Graphics/D3D files (comments + `Debug.WriteLine` strings only).
- **Required whole-repo guards:** `rg "VorticeD3D11"` to enumerate all sites; ensure no code
  depends on the log strings.
- **Graphify usage:** None required.
- **Build commands:** Restore + Debug + Release.
- **Smoke tests:** Build only (no behavior change).
- **Stop conditions:** Any cref needed for doc generation semantics.
- **Exit criteria:** No ghost names; builds green.

### Stage 7 — `DxCdg` → `Cdg` Public Rename (high-care, isolated)

- **Goal:** Align the public CDG naming with current behavior (no separate DX presenter).
- **Risk:** Medium (binary-breaking NuGet API change)
- **Scope:** Rename `DxCdgEngine`→`CdgEngine`, `IDxCdgSnapshotSource`→`ICdgSnapshotSource`,
  internal `DxCdg*` types, DI registration, and all ~65 references — as a single mechanical pass.
- **Do not touch:** Any behavior, shader, or layout; do not combine with feature work.
- **Likely files affected:** entire `Core/DxCdg/`, `NativeMediaPresentationCoordinator.cs`,
  `D3D11MediaCompositionPresenter.cs`, `D3D11CompositionLayerState.cs`, `RenderPipeline.cs`,
  `MediaPlayerServiceProvider.cs`, consuming projects.
- **Required whole-repo guards:** `rg "DxCdg"` across all projects + DI + XAML; confirm public
  API/NuGet impact is approved (Open Question 3).
- **Graphify usage:** Re-map all `DxCdg` references before and after.
- **Build commands:** Restore + Debug + Release across all 3 projects.
- **Smoke tests:** Full CDG/karaoke parity; DI resolves the renamed engine; package builds.
- **Stop conditions:** External consumer breakage not approved; any behavior diff.
- **Exit criteria:** Naming aligned; package version bumped; identical behavior.

### Stage 8 — Optional: Buffer Pooling (VLC-inspired) + Present-Layer Collapse

- **Goal:** Apply VLC's pooling idea more broadly; optionally collapse the FramePresenter pair.
- **Risk:** Medium (renderer)
- **Scope:** Generalize `ArrayPool`/object reuse for frame/snapshot buffers; optionally merge
  `D3D11FramePresenter` + `D3D11PresentationFramePresenter`.
- **Do not touch:** Backend selection, shader, public API.
- **Likely files affected:** frame/upload/composition presenters, `VideoDecoder.cs`.
- **Required whole-repo guards:** Confirm presenter callers (only services class).
- **Graphify usage:** Confirm the wrapper chain has no other callers.
- **Build commands:** Restore + Debug + Release.
- **Smoke tests:** Full video + karaoke parity; allocation/perf comparison.
- **Stop conditions:** Any present regression or buffer aliasing.
- **Exit criteria:** Reduced allocations; optional layer collapse with identical behavior.
