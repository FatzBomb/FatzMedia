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
   wash color (`CdgEngine` → `CdgStateMachine.CreateSnapshot`), plus LINQ/iterator/record
   allocations every present in `D3D11MediaCompositionPresenter.PresentToTargets`.
2. **One blocking-async anti-pattern**: the operation-queue worker runs async load work via
   `operation().GetAwaiter().GetResult()`, tying up the single serialization thread (and
   violating the repo's own no-`.GetResult()` rule).
3. **Remaining naming/comment drift**: a dangling `VorticeD3D11Renderer` doc-cref in ~6 files
   (the class no longer exists) and `[VorticeD3D11…]` log tags across ~10 files. CDG naming has
   already been aligned to the current hardware-composited CDG pipeline and is no longer a roadmap
   issue.
4. **A small number of reliability gaps**: a leaked `CancellationTokenSource` per CDG
   play/seek, missing `ConfigureAwait(false)` on four Core awaits, silent `catch {}` in the
   directory scanner, and no explicit device-removed (`DXGI_ERROR_DEVICE_REMOVED`) recovery
   branch at the present site.
5. **Persistent video stutter still needs an evidence-first investigation**: the prior Stage 11
   timing work fixed two proven causes (coarse `WaitOne(TimeSpan)` pacing and free-running
   source-cadence drift), and current runs have reportedly narrowed drops/selection skips close
   to zero. Any remaining stutter should now be treated as a separate timing/presentation/system
   investigation rather than another blind selector change.
6. **Two deferred architecture cleanup items remain live but awkward**: `RenderPipeline` still
   has a concrete `VideoPipeline` cast, and `ClockAudioEngine` still implements the broader
   `IAudioEngine` role for clock-backed non-audio timing. Both should be roadmap items, not
   dead-code deletions.

Recommended first stage: capture a runtime profiling baseline, then apply the smallest
allocation fix first (`RefreshBackgroundWashUnsafe` reading cached wash color directly). Keep
measurement, allocation fixes, naming cleanup, and behavior changes strictly separate.

The `COMPARISON.md` update adds several roadmap-level quality findings that should be treated
as safety rails before deeper work: automated tests/CI are missing, structured error reporting
is still absent, shared `MediaState` ownership remains distributed, `VideoPipeline.cs` is still a
large multi-concern file, and unsafe FFmpeg lifetime handling deserves a focused audit. Those
items should be added as staged work without diluting the existing profiling/allocation plan.

The active cleanup tracker adds two deferred architecture decisions that should remain in the
roadmap: removing the `RenderPipeline` → concrete `VideoPipeline` cast without leaking renderer
concerns into public `IVideoEngine`, and deciding whether `ClockAudioEngine` should remain a
virtual clock/audio adapter or be split behind narrower transport/clock abstractions.

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
- Purpose-built karaoke strengths versus VLC: first-class CDG graphics, sidecar backgrounds,
  archive pairing, pitch/tempo/normalization, responsive latest-wins seek behavior, and a clean
  .NET-friendly API are core advantages that should be preserved.
- Boundary enforcement scripts are a real architectural asset; keep them current and run them
  before/after WPF or presentation-adjacent changes.

### Cons

- Steady-state allocations on the karaoke/composition hot path (full-framebuffer snapshot to
  read one value; per-present LINQ/iterator/record churn).
- A blocking `GetAwaiter().GetResult()` inside the single operation-queue worker, contrary to
  the project's own async rules.
- Naming/documentation drift remains only in `VorticeD3D11Renderer` ghost crefs and
  `[VorticeD3D11…]` log tags that misrepresent the current design to a new reader.
- A few small reliability gaps (leaked CTS, missing `ConfigureAwait(false)`, silent scan catch,
  no explicit device-lost recovery branch).
- `AGENTS.md` "Known Dead Code" list is stale and now misleading.
- No automated tests or CI exist yet; this is the biggest quality gap before deeper refactors.
- Error handling is still too often `Debug.WriteLine` + `false`, which is not enough for a
  reusable media-control library.
- `VideoPipeline.cs` remains a large multi-concern file that mixes decode, presentation, seek,
  queues, timing, and diagnostics.
- `MediaState` is still mutated from multiple pipelines; the operation queue helps, but final
  public state ownership is not centralized.
- Unsafe FFmpeg pointer/resource lifetime remains an area where a bug can hard-crash the host.
- A remaining visible stutter report exists even after drops and `selectionSkips` were reduced
  close to zero; this likely requires runtime evidence from counters, ETW/WPR/GPUView, present
  pacing logs, DPC/audio checks, and monitor/refresh validation rather than static-only cleanup.
- `RenderPipeline` contains a concrete `VideoPipeline` cast, and `ClockAudioEngine` has a broader
  role than its clock-backed purpose suggests. Both are live architecture-cleanup candidates.

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
- CDG: `CdgEngine` (`ICdgEngine` + `ICdgSnapshotSource`) replays packets into
  `CdgStateMachine`, producing an immutable `CdgRenderSnapshot` (indices + palette + dirty).
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
- **Files:** `Core/Cdg/CdgEngine.cs:236-246` (callers `:45`, `:62`, `:193`);
  `Core/Cdg/CdgStateMachine.cs:145-169`
- **Symbols:** `CdgEngine.RefreshBackgroundWashUnsafe`, `CdgStateMachine.CreateSnapshot`
- **Risk:** Medium (perf, GC pressure; behavior unchanged)
- **Category:** Performance
- **Evidence:** `RefreshBackgroundWashUnsafe` calls `CreateSnapshot(consumeDirtyRegions:false)`,
  which does `_colorIndices.ToArray()` (300×216 = 64,800 bytes) + `_paletteArgb.ToArray()` +
  dirty-rect copy (confirmed `CdgStateMachine.cs:149-150`), yet only reads
  `snapshot.BackgroundWashArgb`. Invoked on lyric changes and every opacity/override setter.
- **Why it matters:** Churns ~64 KB per lyric update during karaoke playback → steady Gen0 GC.
- **Recommendation:** Read the already-exposed cached value `_cdgState.BackgroundWashArgb`
  (`CdgStateMachine.cs:38`) instead of building a snapshot.
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
- **Files:** `Core/Cdg/CdgStateMachine.cs:145-169`;
  `Core/Graphics/NativeMediaPresentationCoordinator.cs` (`TryCreateAndCacheCdgSnapshot :793`)
- **Symbols:** `CdgStateMachine.CreateSnapshot`
- **Risk:** Low-Medium (perf)
- **Category:** Performance
- **Evidence:** `_colorIndices.ToArray()` (64,800 bytes) + `_paletteArgb.ToArray()` each real
  snapshot; retained `_lastCdgSnapshot` keeps one alive.
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

### Finding: `Debug.WriteLine`-only diagnostics are not enough for a reusable library

- **Area:** Cross-cutting diagnostics and host-facing errors
- **Files:** Multiple Core and D3D/pipeline files; specific examples include `DirectoryScanner`,
  swap-chain present failure paths, native presentation detach/teardown paths, native load
  bootstrap messages, and open/load/decode failures that currently return `false` without a
  structured reason.
- **Risk:** High for supportability; Medium for runtime correctness.
- **Category:** Reliability / Observability / Public API
- **Evidence:** `COMPARISON.md` calls this one of FatzBombMedia's weakest architectural choices:
  host apps often receive boolean failure while details are only written to debug output. VLC's
  engine exposes programmatic error details rather than relying only on debugger output.
- **Why it matters:** A host needs to distinguish missing native DLLs, unsupported codecs,
  corrupt archives, decode failures, device loss, and user-cancel/normal stop cases. Debug output
  disappears in normal production usage and cannot drive UI error messages or telemetry.
- **Recommendation:** Yes, move beyond `Debug.WriteLine`, but do it as **logging plus structured
  error surface**, not logging alone:
  - Use `Microsoft.Extensions.Logging`/`ILogger<T>` internally for diagnostic events.
  - Add a typed `MediaErrorCode` / `MediaFailure` / `MediaOperationResult` style surface so host
    apps can display actionable messages without parsing log text.
  - Keep debug logging only as a sink/development aid, not the source of truth.
  - Add log levels: Trace for high-frequency frame details, Debug for diagnostic state changes,
    Information for load/render backend selection, Warning for recoverable failures, Error for
    failed operations, Critical only for unrecoverable native/device failures.
- **Suggested action:** Refactor
- **Stage candidate:** Stage ER — Structured Logging and Error Reporting

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

### Finding: Public event/threading contract needs explicit enforcement

- **Area:** Public API/event delivery
- **Files:** `MediaControlPlayer`, pipeline event sources, presentation-frame events, BASS callback
  and presentation-loop related callbacks.
- **Risk:** Medium
- **Category:** Threading / Public API
- **Evidence:** `COMPARISON.md` notes public events can fire from background/media threads and
  synchronous APIs can block in selected paths, while async APIs are safer. This may be documented
  but should be made explicit and testable.
- **Why it matters:** WPF/WinUI/Avalonia hosts must know which events require UI dispatch. Silent
  cross-thread UI updates are a common integration failure.
- **Recommendation:** Document and optionally expose a configurable dispatcher/synchronization
  strategy at the adapter/host level, without making Core depend on WPF.
- **Suggested action:** Investigate / Document / Test
- **Stage candidate:** Stage ST or Stage ER, depending on whether event delivery is altered.

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

### Finding: Remaining 23.98fps / video playback stutter requires a new evidence pass

- **Area:** Video presentation timing, present pacing, audio/video clock sync, OS/GPU scheduling.
- **Files:** `VideoPipeline.cs`, `VideoPresentationTiming.cs`, `PrecisionWaiter.cs`,
  `NativeMediaPresentationCoordinator.cs`, `PipelineMediaEngine.cs`, D3D11 presentation backends.
- **Symbols:** `selectionSkips`, `lateDrops`, `maxClockLate`, `maxDroppedClockLate`,
  `PrecisionWaiter`, source-cadence presentation loop, present/commit paths.
- **Risk:** Medium-High if changed blindly; Low for measurement-only investigation.
- **Category:** Renderer / Timing / Performance / Reliability.
- **Evidence:** Stage 11 logs showed decode, present, and composition were fast enough; the queue was
  not starving; `lateDrops=0`; and `selectionSkips` was the key failure signal. The corrected root
  cause was not premature frame selection, but coarse `WaitOne(TimeSpan)` pacing and later phase drift
  between a fixed source-cadence loop and the audio-mastered clock. `PrecisionWaiter` plus
  clock-locked source-cadence waits addressed those proven defects. Current user reports say drops
  and `selectionSkips` are now practically zero, but visible stutter still exists somewhere.
- **Why it matters:** If diagnostic counters no longer show frame selection/dropping, the remaining
  stutter may be outside the old selector path: DWM/DirectComposition pacing, monitor refresh mismatch,
  GPU/driver stalls, DPC/ISR spikes, audio clock jitter, background video cadence, power management,
  fullscreen/resize behavior, or occasional blocking/GC pauses.
- **Recommendation:** Add a dedicated measurement-only stage before any new timing code changes.
  Capture synchronized app diagnostics, `dotnet-counters`, CPU/alloc traces, WPR/WPA/GPUView, and
  long-run 23.98fps playback logs. Do not modify selector logic unless captures prove it is still the
  failure source.
- **Suggested action:** Investigate / Profile / Test.
- **Stage candidate:** Stage VT — Video Timing / Stutter Investigation.

### Finding: `RenderPipeline` has a concrete `VideoPipeline` cast

- **Area:** Pipeline abstraction boundary.
- **Files:** `FatzBombMedia.MediaControl/Pipeline/RenderPipeline.cs`, `VideoPipeline.cs`,
  `IVideoEngine` usage sites.
- **Symbols:** `_video is VideoPipeline`, `RenderCurrentFrame`, `IVideoEngine`, `RenderPipeline`.
- **Risk:** Medium.
- **Category:** Architecture / Maintainability.
- **Evidence:** Active cleanup tracking identifies `RenderPipeline.RenderVideoAtCurrentPosition(...)`
  as casting the `_video` abstraction to `VideoPipeline` to call `RenderCurrentFrame(...)`. Current
  behavior works because `PlaybackCoordinator` constructs the in-repo video path as `VideoPipeline`,
  but the cast is an abstraction leak.
- **Why it matters:** Removing the cast the wrong way could pollute public `IVideoEngine` with
  renderer-specific methods or blur the Core/WPF boundary. Keeping it forever preserves a hidden
  coupling that makes later pipeline decomposition harder.
- **Recommendation:** Treat as a later architecture stage. Prefer an internal/narrow render-current
  abstraction or coordinator-owned route; do not add renderer-specific members to public
  `IVideoEngine` just to remove the cast.
- **Suggested action:** Refactor later / Investigate.
- **Stage candidate:** Stage VP-R — Remove RenderPipeline Concrete Cast.

### Finding: `ClockAudioEngine` currently carries too much of the `IAudioEngine` role

- **Area:** Audio, clock, transport, and effects abstraction boundary.
- **Files:** `ClockAudioEngine.cs`, `IAudioEngine.cs`, `PlaybackCoordinator.cs`, `MediaSession.cs`,
  `MediaEffects`.
- **Symbols:** `ClockAudioEngine`, `IAudioEngine`, `ActiveAudio`, transport state, pitch/tempo/
  normalization members.
- **Risk:** Medium-High.
- **Category:** Architecture / Timing / API semantics.
- **Evidence:** Active cleanup tracking notes that `ClockAudioEngine` is live for standalone CDG and
  video-without-audio paths. It implements `IAudioEngine` so these paths can expose clock-backed
  position, duration, tempo, and transport state through the same active-audio facade, while some
  file/audio-specific members are unsupported or not meaningful.
- **Why it matters:** The current shape works, but it mixes real audio playback responsibilities with
  virtual clock/transport responsibilities. A careless cleanup could break standalone CDG or silent
  video timing; a careful split could make effects, normalization, and transport semantics clearer.
- **Recommendation:** Defer until tests/profiling and structured error reporting exist. Decide whether
  to keep `ClockAudioEngine` as a documented virtual adapter or split clock/transport/effects/file-audio
  responsibilities behind narrower interfaces.
- **Suggested action:** Investigate / Refactor later.
- **Stage candidate:** Stage AC — Audio / Clock Responsibility Split.

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
- **Quality infrastructure** (tests, CI, static analyzers, fuzzing/security posture) is far
  stronger than FatzBombMedia's current manual-only validation. The immediate lesson is not to
  adopt VLC's build complexity, but to add tests and automated guards around the focused engine.
- **Structured error model** is stronger: VLC-style error codes/queryable details are more useful
  to hosts than `Debug.WriteLine`-only diagnostics.
- **Concern split in video output** is stronger: VLC separates video output orchestration, display,
  control queues, windowing, subpictures, timing, and statistics into separate areas.

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
3. **Add a test/CI safety net before deeper refactors** — boundary scripts, presentation parity,
   non-renderer unit tests, and a repeatable smoke-test runner. Severity: High.
4. **Introduce structured logging + structured errors** — logging is the right replacement for
   scattered `Debug.WriteLine`, but hosts also need typed error results. Severity: High.
5. **Centralize final state ownership** after error/logging work; let pipelines report observations
   instead of independently owning public state. Severity: Medium-High.
6. **Split `VideoPipeline.cs` by responsibility** after tests/profiling exist. Severity: Medium.
7. **Audit unsafe FFmpeg lifetime** with targeted tests/stress runs. Severity: Medium-High.
8. **Do NOT** import VLC's plugin/object framework, fifo-locking complexity, or general-codec
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
| Q1 | No automated tests/CI | Quality | High | Add safety net |
| ER1 | `Debug.WriteLine`-only diagnostics / weak host error surface | Reliability | High | Refactor |
| ST1 | Distributed mutable `MediaState` ownership | State | Med-High | Refactor |
| FF1 | Unsafe FFmpeg lifetime hardening needed | Native lifetime | Med-High | Audit/Test |
| VP1 | `VideoPipeline.cs` multi-concern monolith | Maintainability | Medium | Refactor later |
| VT1 | Remaining visible stutter after near-zero drops/skips | Timing/Presentation | Med-High | Profile/Test |
| VP-R1 | `RenderPipeline` concrete `VideoPipeline` cast | Architecture | Medium | Refactor later |
| AC1 | `ClockAudioEngine` role overload via `IAudioEngine` | Architecture/Timing | Med-High | Investigate later |
| N1 | `VorticeD3D11Renderer` ghost doc-cref | Naming | Low | Rename |
| N2 | `[VorticeD3D11…]` log prefixes | Naming | Low | Rename |
| DC1 | `IMediaEngine.SeekAsync` unused | Dead Code | Low | Remove/Keep |
| DC2 | `CompatibilityInfo` unread members | Dead Code | Low | Investigate |
| DOC1 | `AGENTS.md` stale "Known Dead Code" | Legacy | Low | Remove (doc) |
| DOC2 | `Comparison.md` references removed classes | Legacy | Low | Remove (doc) |

## 16. Recommended Smoke Tests and Validation

Until Stage 0T exists, no automated tests exist; use `docs/VALIDATION.md` (15-item checklist) plus the
`FatzBombMedia.Player` host. After Stage 0T, run the automated test/build/boundary suite first,
then use the manual host smoke tests for renderer/media behavior. After any stage:

1. `dotnet build FatzBombMedia.sln --no-restore` (zero warnings).
2. `dotnet build -c Release FatzBombMedia.sln --no-restore` (warnings-as-errors).
3. WPF-boundary check: `scripts/check-wpf-boundary.ps1` (Core must stay UI-free).
4. Manual host smoke (requires x64 BASS + FFmpeg DLLs in `FatzBombMedia.Player/native/`):
   - Audio-only playback (volume/pitch/tempo, normalization on/off).
   - Standalone CDG + audio+CDG karaoke (wash color change, lyric updates, seek/scrub).
   - Video with audio (hardware path) + video-without-audio (clock path).
   - Seek/scrub responsiveness; secondary/fullscreen display fan-out.
   - Long 23.98fps playback with app logs showing `selectionSkips`, `lateDrops`, `maxClockLate`,
     `maxDroppedClockLate`, queue depth, FPS, refresh, and present/composition timing.
   - DirectComposition vs. native-HWND fallback (force fallback to verify both backends).

## 17. Open Questions / Human Confirmation Needed

1. Is `IMediaEngine.SeekAsync` intended public API for external consumers, or removable?
2. Is `BinaryCompatibility` meant as a packable diagnostic API, or should it be `internal`?
3. Which automated test level should come first: pure unit tests, smoke-test runner, or CI-only
   build/boundary scripts?
4. Should Core expose errors as `MediaOperationResult` returns, a `LastError` property, events,
   exceptions for exceptional failures, or a combination?
5. Should public events remain background-thread events by contract, or should adapters provide
   optional marshaling?
6. Does any layer already handle `DXGI_ERROR_DEVICE_REMOVED` (driver update/GPU reset), or is a
   re-initialize path genuinely missing?
7. Is the operation-queue load path ever re-entrant (could a queued op await something that needs
   the queue)? Confirms the deadlock risk severity for C1.
8. With current near-zero drops/selection-skips, does the remaining stutter correlate with DWM/
   DirectComposition present timing, monitor refresh mismatch, DPC/ISR spikes, GC pauses, audio clock
   jitter, background-video cadence, or a still-unlogged presentation gap?
9. Should `RenderPipeline` gain an internal/narrow video-render abstraction, or should current-frame
   rendering move fully under the coordinator/video-pipeline ownership?
10. Should `ClockAudioEngine` remain a documented virtual `IAudioEngine` adapter, or should clock,
   transport, effects, and file-audio responsibilities be split behind narrower interfaces?

## 18. Staged Improvement Roadmap

### Roadmap Principles

- One concern per stage; build after each stage.
- Separate dead-code removal, performance, naming, and behavior into distinct stages.
- Keep renderer/present-path changes isolated and high-care.
- Stop and report if any whole-repo guard reveals unexpected live usage.
- Update docs (`CODECLEANUP.md`, `AGENTS.md`, `CURRENT_ARCHITECTURE.md`) after relevant stages.
- Preserve deliberate strengths: D3D11 CDG compositor, DirectComposition split, dirty-rect
  uploads, palette revision tracking, latest-wins seek, sidecar backgrounds, archive pairing,
  pitch/tempo/normalization, and strict Core/WPF separation.
- Treat VLC as a reference for engineering discipline (tests, pooling, structured errors,
  separation of concerns), not as a blueprint for plugins/streaming/subtitles/cross-platform scope.
- Replace `Debug.WriteLine` with structured logging where diagnostic detail is useful, but pair it
  with typed host-facing errors so application code does not parse logs.
- Add safety rails before large architecture refactors: tests/CI first, then logging/errors, then
  state ownership, then video-pipeline decomposition.
- Treat remaining video stutter as an evidence problem first. If drops and `selectionSkips` are
  near zero, do not keep changing selector logic without synchronized runtime traces.
- Keep deferred architecture cleanup separate from dead-code cleanup: `RenderPipeline` cast removal
  and `ClockAudioEngine` role split are later design stages.

### Roadmap Phase Outline

- **Phase 0 — Evidence and safety rails:** baseline, profiling, tests/CI, documentation guards.
- **Phase 1 — Measured hot-path fixes:** small allocation wins first; pooling only after proof.
- **Phase 1T — Timing investigation:** isolate remaining 23.98fps/video stutter with synchronized
  app diagnostics, counters, ETW/WPA/GPUView, and present/clock logs before changing timing code.
- **Phase 2 — Observability and reliability:** structured logging/errors, CTS/await fixes, device-loss investigation.
- **Phase 3 — State/threading architecture:** central state owner and operation-queue async pump.
- **Phase 4 — Maintainability refactors:** VideoPipeline split, `RenderPipeline` cast removal,
  audio/clock abstraction cleanup, optional presenter collapse, broader pooling.
- **Phase 5 — Deferred/non-goals:** no plugin architecture, streaming, disc playback, subtitle engine, or cross-platform work unless product goals change.

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

### Stage 0P — Runtime Profiling Baseline (measurement only)

- **Goal:** Capture repeatable baseline data before changing hot paths so allocation/performance
  severity is measured, not just inferred from static analysis.
- **Risk:** Low (measurement only)
- **Scope:** Build Release x64, run controlled playback scenarios, capture CPU, managed
  allocations, GC/heap behavior, thread contention/blocking, exceptions, and GPU/presentation
  traces where possible.
- **Do not touch:** Source code, project files, renderer logic, naming.
- **Likely files affected:** None, except optional `profiling/` output folder if you decide to
  keep captured `.nettrace`, `.gcdump`, `.etl`, screenshots, and notes in a local ignored folder.
- **Required whole-repo guards:** None.
- **Graphify usage:** Not required; this is runtime measurement.
- **Build commands:** `dotnet build -c Release FatzBombMedia.sln --no-restore`.
- **Smoke tests:** Same media/scenarios listed in Section 19 Runtime Profiling Plan.
- **Stop conditions:** Native DLLs missing, app cannot launch, or profiler overhead makes playback
  unusable. Fix environment first; do not change code as part of this stage.
- **Exit criteria:** Baseline captures exist for CPU, allocations, counters, GC heap snapshots,
  and at least one graphics/presentation trace.


### Stage VT — Video Timing / Stutter Investigation (measurement only)

- **Goal:** Identify the remaining visible stutter source after Stage 11 timing fixes and after drops
  / `selectionSkips` have reportedly been reduced close to zero.
- **Risk:** Low for measurement; Medium-High for any later timing changes.
- **Scope:** Capture synchronized diagnostics for long 23.98fps playback and other problematic
  files. Correlate app frame-selection counters with OS/GPU/present/audio/GC evidence before changing
  any selector, pacing, or renderer code.
- **Do not touch:** `ShouldAdvanceSelectionToFrame`, `VideoPresentationTiming`, source-cadence
  pacing, `PrecisionWaiter`, DirectComposition/native HWND behavior, or renderer code in this stage.
- **Likely files affected:** None. Optional local ignored profiling output only.
- **Required whole-repo guards:** None for measurement. If a later fix is proposed, trace all timing
  and present counters first: `selectionSkips`, `lateDrops`, `maxClockLate`, `maxDroppedClockLate`,
  `avgTickGap`, `PresentPerf`, `COMPOSITION-D3D11`, `PrecisionWaiter`, and audio-clock sampling.
- **Graphify usage:** Not required; runtime traces are more important.
- **Build commands:** `dotnet build -c Release FatzBombMedia.sln --no-restore`.
- **Profiling captures:**
  - App diagnostic logs for at least 10 minutes of 23.98fps playback.
  - `dotnet-counters` for allocation rate, GC, exceptions, CPU, and thread-pool behavior.
  - Visual Studio or PerfView allocation trace if stutter correlates with GC/alloc spikes.
  - `dotnet-trace` CPU trace for the same file and time window.
  - WPR/WPA/GPUView trace for DWM/DirectComposition/present pacing, GPU queue bubbles, DPC/ISR
    spikes, and CPU/GPU wait relationships.
  - Monitor/refresh validation for the exact display/window used by the presenter.
- **Smoke tests:** Problem file normal playback, audio+CDG, video-only, background video, secondary
  output, resize/fullscreen, seek, and long-run playback.
- **Stop conditions:** If the traces do not identify a correlation, do not change timing code. Add
  more instrumentation first.
- **Exit criteria:** A written stutter hypothesis exists with evidence identifying one or more of:
  present pacing/DWM, monitor refresh mismatch, DPC/ISR/audio glitching, GC/allocation spike,
  background-video cadence, occasional blocking, device/driver behavior, or a still-valid video
  selection/pacing defect.

### Stage 0T — Test and CI Safety Net

- **Goal:** Add basic automated safety rails before deeper renderer/state/API refactors.
- **Risk:** Low-Medium (new test/build infrastructure; should not alter runtime behavior)
- **Scope:** Create a test project and/or scripts that can run repeatable build/guard checks.
  Start with non-renderer and deterministic areas before attempting GPU integration tests.
- **Do not touch:** Runtime behavior, renderer logic, public API shape, allocation fixes.
- **Likely files affected:** New test project, solution file, scripts, optional CI workflow, docs.
- **Initial test candidates:** `CdgStateMachine` packet behavior, archive audio/CDG pairing,
  file type detection, sidecar/background resolution, configuration validation, `MediaState`
  transition expectations, and any pure helper methods extracted during later stages.
- **Guard scripts to automate:** Debug build, Release build, `check-wpf-boundary.ps1`,
  `check-presentation-parity.ps1`, and any no-`Debug.WriteLine`/logging migration checks added later.
- **Required whole-repo guards:** Verify test project references do not introduce WPF dependencies
  into Core or renderer dependencies into the WPF adapter.
- **Graphify usage:** Optional; useful for selecting pure unit-test seams.
- **Build commands:** Restore + Debug + Release builds; run tests if added.
- **Smoke tests:** Manual media smoke remains required for native/GPU paths.
- **Stop conditions:** Test infrastructure requires native DLLs for all tests, introduces flaky GPU
  dependencies too early, or changes product code to make tests pass.
- **Exit criteria:** At minimum, automated build + boundary checks are runnable with one command;
  first deterministic unit tests exist.

### Stage 1A — CDG Wash-Color Allocation Fix (smallest safe win)

- **Goal:** Remove the full-frame snapshot allocation used only to read `BackgroundWashArgb`.
- **Risk:** Low-Medium (hot path, but behavior should be identical)
- **Scope:** A1 only: change `CdgEngine.RefreshBackgroundWashUnsafe` to read the cached
  `_cdgState.BackgroundWashArgb` value directly instead of calling `CreateSnapshot(false)`.
- **Do not touch:** Snapshot structure, compositor, dirty rects, shader logic, backend selection,
  public API, naming.
- **Likely files affected:** `Core/Cdg/CdgEngine.cs`; optional comment in
  `CdgStateMachine.cs` if needed.
- **Required whole-repo guards:** Verify all `BackgroundWashArgb`, `RefreshBackgroundWashUnsafe`,
  and `CreateSnapshot` callers before changing semantics.
- **Graphify usage:** Optional; confirm no alternate wash-color readers are bypassed.
- **Build commands:** Debug + Release builds.
- **Smoke tests:** Audio+CDG and standalone CDG: initial background color, lyric updates, wash
  opacity changes, override changes, seek/scrub, stop/clear.
- **Stop conditions:** Any wash-color visual mismatch or stale color after seek/override.
- **Exit criteria:** Same visual behavior; allocation profile shows the snapshot allocation is gone
  from the wash-refresh path.

### Stage 1B — Small Per-Frame Allocation Fixes (no pooling yet)

- **Goal:** Remove simple per-frame allocations that do not require ownership/lifetime redesign.
- **Risk:** Low-Medium
- **Scope:** A4/A5 lightweight fixes only: avoid array materialization in seek-frame inspection;
  reuse/cached single-element `VideoProcessorStream[]` where safe; cache size constants.
- **Do not touch:** CDG snapshot pooling, compositor layer scheduling, renderer backend selection,
  shader logic, public API.
- **Likely files affected:** `VideoDecoder.cs`, `VideoPipeline.cs`,
  `D3D11VideoProcessorPresenter.cs`, `D3D11MediaCompositionPresenter.cs`.
- **Required whole-repo guards:** Verify `GetQueuedFramesSnapshot` callers and any assumptions
  about returned snapshot immutability.
- **Graphify usage:** Optional; map seek-decode call path.
- **Build commands:** Debug + Release builds.
- **Smoke tests:** Seek/scrub stress, video-only playback, hardware video path, video-without-audio
  clock path.
- **Stop conditions:** Seek target regression, queue enumeration race, video present regression.
- **Exit criteria:** Lower allocation rate during seek/video playback with identical behavior.

### Stage 1C — Composition Layer Allocation Cleanup (scratch lists / no LINQ churn)

- **Goal:** Reduce per-present LINQ/iterator/list/record churn in composition without changing
  output ordering or layer semantics.
- **Risk:** Medium (composition hot path)
- **Scope:** A2 only: replace repeated `Where/Select/ToList/FirstOrDefault` hot-path work with
  cached descriptors, direct id lookup, and reusable scratch lists/index loops.
- **Do not touch:** Layer ids, draw order, shader logic, dirty rect behavior, backend selection,
  DirectComposition/native HWND behavior.
- **Likely files affected:** `D3D11MediaCompositionPresenter.cs`,
  `D3D11CompositionLayerCache.cs`, `MediaCompositionFrame.Layers.cs`.
- **Required whole-repo guards:** Verify `EnumerateLayers`, `CreateUpdatePlan`, `Capture`, and
  `MarkPlanConsumed` callers before changing iteration behavior.
- **Graphify usage:** Confirm composition frame/layer call graph before and after.
- **Build commands:** Debug + Release builds.
- **Smoke tests:** CDG-only, audio+CDG, sidecar image background, sidecar video background,
  wash overlay, primary + secondary output, resize/fullscreen.
- **Stop conditions:** Layer order/visibility/dirty-region regression or retained-layer cache bug.
- **Exit criteria:** Allocation profile improves during composited playback; visual parity holds.

### Stage 1D — CDG Snapshot Buffer Pooling (only after profiling proves need)

- **Goal:** Reduce legitimate CDG snapshot buffer copies/allocations using explicit ownership rules.
- **Risk:** Medium-High (buffer lifetime/aliasing risk)
- **Scope:** A3 only: evaluate `ArrayPool<byte>`/`ArrayPool<int>` or a small snapshot-buffer pool
  for index/palette data. Keep this isolated from 1A–1C.
- **Do not touch:** Shader logic, dirty rect semantics, presentation backend selection, unrelated
  allocation fixes, naming.
- **Likely files affected:** `CdgStateMachine.cs`, `CdgRenderSnapshot.cs`,
  `NativeMediaPresentationCoordinator.cs`, `D3D11MediaCompositionPresenter.cs`.
- **Required whole-repo guards:** Map snapshot creation, caching, upload, and disposal/lifetime.
- **Graphify usage:** Required; trace `TryCreateCdgSnapshot` → cache → compositor upload.
- **Build commands:** Debug + Release builds.
- **Smoke tests:** Long CDG playback, rapid seek/scrub, repeated load/close, secondary output,
  forced GC while playing if practical.
- **Stop conditions:** Any stale lyrics, palette corruption, dirty rect aliasing, or retained buffer
  reused before upload completes.
- **Exit criteria:** Proven allocation reduction with no visual corruption or lifetime bug.

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

### Stage ER — Structured Logging and Host-Facing Error Reporting

- **Goal:** Replace scattered `Debug.WriteLine`-only diagnostics with structured logging plus a
  typed error surface that host applications can use.
- **Risk:** Medium (cross-cutting diagnostics/API behavior)
- **Scope:** Introduce `ILogger<T>` usage or an internal logging abstraction backed by
  `Microsoft.Extensions.Logging`; define `MediaErrorCode`/`MediaFailure`/`MediaOperationResult`
  or equivalent host-facing error detail. Migrate high-value failure paths first: native DLL load,
  open/load, archive scan, FFmpeg decode/open, BASS open/playback, background media load,
  presentation backend selection/failure, and device-removed detection.
- **Do not touch:** Renderer behavior, state ownership architecture, operation-queue semantics,
  allocation fixes, or broad API redesign beyond the error/logging surface.
- **Likely files affected:** `Bootstrap`, `DirectoryScanner`, media open/load paths,
  `MediaControlPlayer`, audio/video/CDG pipelines, D3D11 present/backends, Player host display
  of errors if included.
- **Required whole-repo guards:** Search all `Debug.WriteLine`, `catch`, `return false`,
  `LastNativeLoadMessage`, and failure-result paths. Classify by log level and whether host-facing
  error detail is required.
- **Graphify usage:** Useful to map failure propagation from low-level subsystems to public API.
- **Build commands:** Restore + Debug + Release builds; run tests if Stage 0T exists.
- **Smoke tests:** Missing native DLLs, unsupported/corrupt file, inaccessible archive path,
  missing sidecar background, forced scanner access denial, present failure/device-loss simulation
  if possible.
- **Stop conditions:** Host API becomes harder to use, log spam appears on per-frame paths, or
  diagnostics allocate significantly on hot paths.
- **Exit criteria:** Host can retrieve/display meaningful error details; diagnostics use structured
  logs with appropriate levels; `Debug.WriteLine` remains only as an optional/development sink.

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

### Stage FF — FFmpeg Lifetime and Native Resource Safety Audit

- **Goal:** Reduce hard-crash risk around unsafe FFmpeg pointers and native resource ownership.
- **Risk:** Medium-High (unsafe/native lifetime)
- **Scope:** Audit `AVFrame*`, `AVPacket*`, `AVCodecContext*`, hardware frames, temp-file use,
  and decoder close/error paths. Add SafeHandle-style wrappers where practical and tests/stress
  checks where possible.
- **Do not touch:** Decoder behavior, presentation backend selection, allocation pooling, public API
  unless a lifetime bug requires a narrow fix.
- **Likely files affected:** `VideoDecoder.cs`, `VideoAudioEngine.cs`, hardware-frame ownership
  helpers, FFmpeg interop cleanup code, tests.
- **Required whole-repo guards:** Map every native allocation/free, frame ownership transfer,
  `Dispose`, `Close`, failure path, and repeated load/close/seek scenario.
- **Graphify usage:** Useful to map decoder ownership and call chains.
- **Build commands:** Restore + Debug + Release; run tests if available.
- **Smoke tests:** Repeated load/close, rapid seek/scrub, corrupt media, unsupported codec, hardware
  decode on/off, video with and without audio, long-run playback.
- **Stop conditions:** Any ownership uncertainty that cannot be validated; defer broad wrapper work
  until tests/profiling exist.
- **Exit criteria:** Ownership rules are documented; obvious double-free/use-after-free risks are
  removed or guarded; stress tests/notes exist.


### Stage AC — Audio / Clock Responsibility Split (deferred architecture)

- **Goal:** Clarify the difference between real audio playback and virtual clock-driven playback so
  `ClockAudioEngine` no longer has to carry unrelated file/audio responsibilities unless it remains
  intentionally documented as an adapter.
- **Risk:** Medium-High (standalone CDG and video-without-audio timing are live paths).
- **Scope:** Inventory which consumers need transport state, clock position/duration, tempo, real
  audio effects, file/audio decoding, normalization, and completion events. Decide whether to keep
  the current adapter shape or introduce a narrower clock/transport abstraction.
- **Do not touch:** CDG timing behavior, video-without-audio behavior, real audio effects behavior,
  broad `IAudioEngine` changes, or public API semantics without a staged migration.
- **Likely files affected:** `ClockAudioEngine.cs`, `IAudioEngine.cs`, `PlaybackCoordinator.cs`,
  `MediaSession.cs`, `MediaEffects`, tests.
- **Required whole-repo guards:**
  `ClockAudioEngine|ActiveAudio|IAudioEngine|MediaEffects|SetVolume|SetPitch|SetTempo|Normalize|NormalizationGain|AnalyzePeak|PlayHandle|PlaybackEnded|PositionChanged`, plus construction/selection guards for `new ClockAudioEngine`, `_session.ClockAudio`, and content-flag checks.
- **Graphify usage:** Useful to map audio/clock/effects consumers.
- **Build commands:** Restore + Debug + Release; run tests if available.
- **Smoke tests:** Standalone CDG, video-without-audio, audio-only, audio+CDG, video with audio,
  pitch/tempo/volume/normalize behavior, play/pause/seek/stop/reload.
- **Stop conditions:** Any uncertainty about timing ownership or effects routing; defer until tests
  and profiling exist.
- **Exit criteria:** Either the current virtual adapter is intentionally documented and unsupported
  operations fail clearly, or clock/transport/file-audio responsibilities are split cleanly without
  behavior regressions.

### Stage ST — Centralize Playback State Ownership

- **Goal:** Move from distributed pipeline mutation of final public state toward one reconciled
  session/coordinator state owner.
- **Risk:** Medium-High (public state semantics and race behavior)
- **Scope:** Keep `MediaControlOperationQueue` as serialization boundary where useful, but have
  pipelines report observations/events rather than independently owning final playback state,
  error state, source path, position, duration, and content flags.
- **Do not touch:** Renderer behavior, CDG snapshot semantics, allocation fixes, FFmpeg lifetime,
  public API naming.
- **Likely files affected:** `MediaState`, `PlaybackCoordinator`, `AudioPipeline`, `CdgPipeline`,
  `VideoPipeline`, `MediaControlPlayer`, tests.
- **Required whole-repo guards:** Map all `MediaState.Set*` calls and all public state readers;
  identify which thread each write can occur on.
- **Graphify usage:** Useful to visualize state mutation paths.
- **Build commands:** Restore + Debug + Release; run tests if Stage 0T exists.
- **Smoke tests:** Load/play/pause/stop/seek across audio-only, audio+CDG, video, video-without-audio,
  error cases, and repeated load/close.
- **Stop conditions:** Any ambiguous owner for a state transition, event ordering change, or UI state regression.
- **Exit criteria:** One owner reconciles public state; pipelines no longer race to set final public state.

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


### Stage VP-R — Remove `RenderPipeline` → Concrete `VideoPipeline` Cast (deferred architecture)

- **Goal:** Remove the concrete cast in `RenderPipeline.RenderVideoAtCurrentPosition(...)` without
  leaking renderer-specific methods into the public `IVideoEngine` contract.
- **Risk:** Medium (abstraction cleanup near video/render pipeline boundary).
- **Scope:** Trace all `RenderPipeline`, `VideoPipeline`, `IVideoEngine`, and `RenderCurrentFrame`
  usage. Decide whether the operation belongs behind an internal interface, a coordinator-owned path,
  or a smaller internal video presentation abstraction.
- **Do not touch:** WPF adapter access, renderer behavior, public `IVideoEngine` unless explicitly
  approved, CDG composition semantics, timing behavior, or video decode behavior.
- **Likely files affected:** `RenderPipeline.cs`, `VideoPipeline.cs`, possible new internal interface
  / helper, tests.
- **Required whole-repo guards:**
  `_video\s+is\s+VideoPipeline|RenderCurrentFrame|IVideoEngine|RenderPipeline|new\s+VideoPipeline|Attach\(` across C#, XAML, project files, scripts, docs, and config. Also verify WPF adapter has no `FatzBombMedia.MediaControl.Core.(Cdg|Graphics|Video)` / pipeline imports.
- **Graphify usage:** Recommended before and after to prove coupling is reduced.
- **Build commands:** Restore + Debug + Release; run tests if available.
- **Smoke tests:** Video render at current position, seek/scrub, pause/resume, video without audio,
  CDG/video composition, background video, secondary output.
- **Stop conditions:** Any proposal that adds renderer-specific members to public `IVideoEngine`,
  broadens WPF adapter knowledge, or changes presentation behavior.
- **Exit criteria:** No concrete `VideoPipeline` cast remains in `RenderPipeline`; behavior unchanged;
  boundary scripts pass.

### Stage VP — Split `VideoPipeline.cs` by Responsibility

- **Goal:** Improve maintainability by separating decode, presentation, seek, frame queue, timing,
  constants, and diagnostics responsibilities currently concentrated in `VideoPipeline.cs`.
- **Risk:** Medium-High (large refactor; behavior must remain identical)
- **Scope:** Extract one responsibility at a time: seek worker/pump, frame queue, decode loop
  orchestration, presentation loop orchestration, diagnostics/timing constants. Each extraction
  should be buildable and behavior-preserving.
- **Do not touch:** Renderer backend behavior, D3D11 shader/present code, CDG snapshot semantics,
  public API, pooling unless specifically in a later stage.
- **Likely files affected:** `VideoPipeline.cs` plus new focused helper classes/files.
- **Required whole-repo guards:** Map all callers/events/state writes before each extraction;
  verify no WPF adapter dependencies are introduced.
- **Graphify usage:** Recommended before and after each extraction to ensure dependencies shrink
  rather than move around blindly.
- **Build commands:** Restore + Debug + Release; run tests if available.
- **Smoke tests:** Full video playback, video-without-audio, seek/scrub, pause/resume, background
  video, secondary output, long-run playback.
- **Stop conditions:** Behavior change, seek responsiveness regression, clock drift, or larger
  coupling after extraction.
- **Exit criteria:** `VideoPipeline` becomes an orchestrator with clear collaborators; no behavior regressions.

### Stage 7 — Optional: Buffer Pooling (VLC-inspired) + Present-Layer Collapse

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


## 18.1 Deliberate Non-Goals / Deferred VLC-Style Features

These should not be added to the roadmap unless product goals change:

- VLC-style plugin/module architecture.
- Streaming/network playback.
- Disc/DVD/Blu-ray playback.
- General subtitle/SPU engine beyond CDG karaoke graphics.
- Cross-platform renderer abstraction beyond future adapter experiments.
- Full VLC-style audio filter/effects chain beyond karaoke-focused pitch, tempo, and normalization.
- Playlist/library management inside the media engine; host app should continue to own that.
- Thread-count reduction as a priority; current named threads are acceptable for the focused workload
  unless profiling proves overhead or contention.

These are not deficiencies for the current Windows karaoke/trivia engine. They are scope boundaries.


## 19. Runtime Profiling Plan

### Goal

Turn the static findings in this report into measured evidence. Run the same Release build and
same media scenarios before and after each performance/reliability stage. Keep captures in a local
ignored folder such as `profiling\2026-06-30\`.

### Test matrix

Use stable sample media and repeat each scenario with the same duration:

| Scenario | Duration | Purpose |
|---|---:|---|
| A. Audio-only | 2–5 min | BASS path, normalization, pitch/tempo, no render load |
| B. Standalone CDG | 2–5 min | clock-backed CDG path, completion monitor, wash changes |
| C. Audio + CDG | 5–10 min | main karaoke path, CDG snapshots, wash, lyric updates |
| D. Audio + CDG + image background | 5–10 min | composition layers, retained background, wash/overlay |
| E. Audio + CDG + video background | 5–10 min | video background + CDG composition path |
| F. Video-only with audio | 5–10 min | hardware video path + audio clock sync |
| G. Video without audio | 2–5 min | `ClockAudioEngine` timing path |
| H. Seek/scrub stress | 30–60 sec | seek queue, decoder queue snapshots, race/stall detection |
| I. Secondary/projector output | 2–5 min | fan-out targets, resize/fullscreen, swap-chain behavior |
| J. Long 23.98fps stutter repro | 10–20 min | remaining visible stutter after near-zero drops/skips; app logs + counters + WPR |

### Build and launch baseline

```powershell
cd C:\Programming\FatzBombMedia

dotnet restore FatzBombMedia.sln
dotnet build -c Release FatzBombMedia.sln --no-restore

New-Item -ItemType Directory -Force .\profiling\baseline
# Launch the Release Player normally, with BASS/FFmpeg native DLLs available.
```

### Pass 1 — Live counters during every scenario

Use this first because it is low overhead and immediately shows CPU, allocation rate, GC churn,
exceptions, and thread-pool behavior.

```powershell
dotnet tool update --global dotnet-counters

dotnet-counters ps
# Find the FatzBombMedia.Player PID.

$pid = <PID>
dotnet-counters monitor --process-id $pid System.Runtime --refresh-interval 1
```

Record notes for each scenario:

- CPU usage range
- allocation rate during idle vs playback vs seek
- GC heap size trend
- Gen0/Gen1/Gen2 count growth
- exception count growth
- thread-pool queue length / thread count

### Pass 2 — CPU trace for hot-path call stacks

Run one trace per scenario category rather than one giant trace.

```powershell
dotnet tool update --global dotnet-trace

$pid = <PID>
dotnet-trace collect --process-id $pid --profile cpu-sampling --duration 00:03:00 --output .\profiling\baseline\audio-cdg-cpu.nettrace
```

Open `.nettrace` in Visual Studio or PerfView and search for:

- `CdgEngine.RefreshBackgroundWashUnsafe`
- `CdgStateMachine.CreateSnapshot`
- `D3D11MediaCompositionPresenter.PresentToTargets`
- `D3D11CompositionLayerCache.Capture`
- `MediaCompositionFrame.EnumerateLayers`
- `VideoDecoder.GetQueuedFramesSnapshot`
- `VideoPipeline.DecodeForSeekTarget`
- `MediaControlOperationQueue`

### Pass 3 — Allocation profiling

Use Visual Studio Performance Profiler for the clearest allocation call stacks:

1. Build Release x64.
2. Open **Debug > Performance Profiler**.
3. Select **.NET Object Allocation Tracking**.
4. Start the Release `FatzBombMedia.Player`.
5. Run scenario C, D, and H separately.
6. Stop collection and inspect allocation call trees.

Expected suspects to verify:

- `byte[]` allocations from `CdgStateMachine.CreateSnapshot`
- LINQ/list/iterator allocations from `D3D11MediaCompositionPresenter` and
  `D3D11CompositionLayerCache`
- array allocations from `VideoDecoder.GetQueuedFramesSnapshot`
- per-frame event args or small graphics structs if visible

### Pass 4 — GC heap snapshots for leak checks

Use this after long playback or repeated load/seek cycles.

```powershell
dotnet tool update --global dotnet-gcdump

$pid = <PID>
dotnet-gcdump collect --process-id $pid --output .\profiling\baseline\before.gcdump
# Run 10–20 minutes of audio+CDG, seek/scrub, and repeated load/stop/close.
dotnet-gcdump collect --process-id $pid --output .\profiling\baseline\after.gcdump
```

Compare in Visual Studio or PerfView. Look for growth in:

- `CancellationTokenSource`
- CDG snapshot/index/palette arrays
- D3D/Vortice wrapper objects
- event handler targets
- queued frame/event objects

### Pass 5 — Blocking/threading snapshot during stalls

If playback hangs, load stalls, or seeking blocks:

```powershell
dotnet tool update --global dotnet-stack

$pid = <PID>
dotnet-stack report --process-id $pid > .\profiling\baseline\stacks-during-stall.txt
```

Look for:

- `MediaControlOperationQueue.Run`
- `.GetAwaiter().GetResult()`
- `Task.Delay` waits in normalization
- UI thread waiting on media operations
- decode/present threads blocked on locks

### Pass 6 — Windows/GPU presentation trace

Use this for renderer, DirectComposition/native HWND, swap-chain, resize/fullscreen, and dropped
frame/stutter questions.

```powershell
New-Item -ItemType Directory -Force .\profiling\baseline
wpr -start GeneralProfile -filemode
# Run scenario I: primary + secondary output, fullscreen/resize, video+CDG.
wpr -stop .\profiling\baseline\presentation.etl
```

Open the `.etl` in Windows Performance Analyzer. For deeper GPU/DirectX queue analysis, open the
trace in GPUView if installed with Windows Performance Toolkit. Focus on:

- CPU thread stalls vs GPU queue stalls
- DWM/DirectComposition timing
- present intervals and missed frames
- DPC/ISR spikes during audio/video glitches
- resize/fullscreen timing


### Pass 6B — Dedicated 23.98fps stutter capture

Use this when visible stutter remains even though `lateDrops` and `selectionSkips` are near zero.
Run the exact file/display/output mode that still shows the issue.

Capture together where practical:

1. **App diagnostic log** for at least 10 minutes, including:
   - `selectionSkips`
   - `lateDrops`
   - `maxClockLate`
   - `maxDroppedClockLate`
   - `avgTickGap` / max tick gap
   - queue depth
   - FPS and source frame duration
   - display refresh
   - present/composition timings
   - timer mode (`high-res` vs fallback)
2. **`dotnet-counters`** for CPU, allocation rate, GC, exceptions, and thread-pool health.
3. **`dotnet-trace` CPU sampling** over the same scenario window.
4. **WPR/WPA or GPUView** for DWM/DirectComposition/GPU/present pacing and DPC/ISR spikes.
5. **Monitor validation**: confirm the window is on the intended display and refresh rate, and note
   whether the run uses DirectComposition primary or native-HWND fallback.

Interpretation rules:

- If app counters stay clean but WPR/GPUView shows DWM/GPU queue/present irregularity, treat it as
  presentation/driver/display investigation, not selector logic.
- If counters show GC/allocation spikes at visible stutters, prioritize allocation stages before
  timing changes.
- If audio/DPC spikes align with stutter, investigate audio callbacks/device/driver scheduling.
- If `selectionSkips` starts increasing again with `lateDrops=0`, revisit source-cadence phase lock
  and selector diagnostics using the Stage 11 evidence.
- If no correlation appears, add targeted instrumentation before changing code.

### Pass 7 — PerfView all-in-one capture for hard cases

Use PerfView when Visual Studio/dotnet tools disagree or you need one ETW view for CPU + GC +
threading.

```powershell
PerfView collect /AcceptEULA /NoGui /CircularMB:1024 /MaxCollectSec:180 /Zip:true .\profiling\baseline\fatzbombmedia.etl.zip
```

Open the result and inspect:

- CPU Stacks
- GCStats
- Alloc Stacks
- Thread Time
- Exceptions

### Compare before/after each optimization

For every performance stage:

1. Capture baseline for the affected scenario.
2. Apply exactly one stage.
3. Build Release.
4. Capture the same scenario again.
5. Compare allocation rate, Gen0 frequency, CPU samples, frame/present smoothness, and visual parity.

Do not treat a static optimization as successful until at least one runtime capture shows that it
reduced the intended cost without causing visual or stability regressions.
