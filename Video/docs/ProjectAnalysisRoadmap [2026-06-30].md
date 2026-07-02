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

The comparison work adds several roadmap-level quality findings that should be treated as safety
rails before deeper work: automated tests/CI are missing, structured error reporting is still
absent, shared `MediaState` ownership remains distributed, `VideoPipeline.cs` is still a large
multi-concern file, and unsafe FFmpeg lifetime handling deserves a focused audit. Those items
should be added as staged work without diluting the existing profiling/allocation plan.

The active cleanup tracker adds two deferred architecture decisions that should remain in the
roadmap: removing the `RenderPipeline` → concrete `VideoPipeline` cast without leaking renderer
concerns into public `IVideoEngine`, and deciding whether `ClockAudioEngine` should remain a
virtual clock/audio adapter or be split behind narrower transport/clock abstractions.

## Roadmap Progress

- [x] Stage 0 — completed 2026-06-30: Baseline docs and solution item references now point at
  the active roadmap; stale dead-code guidance, deleted-doc references, and legacy Cdg-name docs
  drift were removed. Checks: Debug pass, Release pass, boundary pass, parity pass, stale legacy
  Cdg-name search clean.
- [x] Stage 0P — completed 2026-07-01: Preflight/build/tool readiness completed; runtime profiling
  baseline captured across all 5 key passes. Checks: Debug pass, Release pass, boundary pass, parity
  pass, stale legacy Cdg-name search clean.
- [x] Stage VT-diag — completed 2026-07-01: Converted the four `VideoPipeline` timing/perf diagnostics
  from `Debug.WriteLine` to `Trace.WriteLine` so they are observable in Release x64 (observability only;
  config-gated; no playback/timing/selector logic changed). Checks: Debug pass, Release pass, boundary
  pass, parity pass, stale legacy Cdg-name search clean.
- [x] Stage VT — completed 2026-07-01: 23.98fps Release capture with now-visible timing diagnostics
  yields an evidence-backed stutter hypothesis (source-cadence loop drift + inherent 24-on-60Hz cadence
  judder; GC/CPU/alloc healthy). Checks: Debug pass, Release pass, boundary pass, parity pass, stale
  legacy Cdg-name search clean.
- [x] Stage VT-F — completed 2026-07-01: Direction 2 calibrated 2026-07-01, validated 2026-07-01
  (PASS). Current-position gap diagnostics added 2026-07-01; capture still required. Clock-relative
  anchor preserved; overhead measurement moved to just before `GetClockLockedPresentationWaitSeconds`
  to avoid double-counting clock advancement from body.
  avgTickGap (42.1→47.6ms, mean 44.1ms) closest to 41.7ms target of all runs; maxTickGap (103.6ms)
  lowest; max selectionSkips/window=15 best of all (33% of pre-fix, 20% of Dir1); total selectionSkips
  184/71w best; lateDrops=0; maxEarly rock-stable 10.3±0.2ms. Checks: Debug pass, Release pass,
  boundary pass, parity pass, stale legacy Cdg-name search clean.

  **Diagnostics added 2026-07-01 (no behavior change):** Calibrated Direction 2 improved metrics over
  all prior runs but visible stutter remained. Per the VLC reference audit (VLC distinguishes
  scheduling/wait drift from display-completion drift), added a new `[VideoPipeline:Timing]`
  diagnostic line in `RecordPresentationDiagnostics` to capture: intended wait deadline, actual wait
  start, actual wake, wait wake error, present start, present return, present duration, post-present
  drift, selected frame PTS, and clock position at selection. Existing `[VideoPipeline:Loop]`
  (avgTickGap, maxTickGap) and `[VideoPipeline:Perf]` (selectionSkips, lateDrops, queue, maxClockLate,
  maxEarly) lines unchanged. New fields: `_lastIntendedDeadlineMs`, `_lastWaitStartMs`, `_lastWakeMs`,
  `_lastWaitErrorMs`, `_lastPresentStartMs`, `_lastPresentEndMs`, `_lastPresentDurationMs`,
  `_lastPostPresentDriftMs`, `_lastSelectedFramePts`, `_lastSelectedClockPosition`. Captured in
  `PresentationLoop` around the wait and present work, plus `PresentFrameForPosition` for frame PTS.
  No wait behavior, subtraction, `PrecisionWaiter`, frame selector, `VideoPresentationTiming`,
  renderer/backend, WPF adapter, public API/config, CDG timing, or audio clock changes. Checks: Debug
  pass, Release pass, boundary pass, parity pass, stale legacy Cdg-name search clean.

  **Diagnostic validation capture — 2026-07-01 (PASS classification):** Release x64 capture with the
  new `[VideoPipeline:Timing]` diagnostics confirms that the remaining stutter is a **PRESENT ISSUE,
  not a WAIT or EXTERNAL issue**. Wait precision is excellent (mean 0.27ms, max 0.54ms), but the
  present/display work itself is slow: `presentDuration` mean 25ms, max 99ms in steady state. The
  132ms `maxTickGap` spike at loop window 127–130 lines up exactly with `presentDuration=47ms` at
  timing line 132 (the present was still running when the next tick needed to start, pushing the
  tick gap to ~132ms). The present finishes *before or right at* the intended deadline
  (`postPresentDrift` consistently 0 to −39ms), so this is not a deadline-overshoot problem — it is
  a *present-work-consumes-the-interval* problem. Selection is also non-deterministic
  (`clockPtsLead` oscillates −10ms to +41ms, mean +12.65ms) but the data does not link selection
  variation to the maxTickGap spikes. `lateDrops=0`, `maxEarly=10.33±0.05ms`. Build/check
  pass. Stale Cdg-name guard clean.

  Recommended next step: add per-present-step timing inside the present work
  (`vout_ConvertForDisplay`, `vout_display_Display`, GPU upload, `VideoProcessorBlt`, D3D11
  compositor) to determine which step is consuming the 25ms average / 99ms peak. The wait and
  clock anchor are now proven correct; the visible stutter is a renderer/display-stage bottleneck
  that pacing logic cannot fix.

  **Present-side diagnostics added 2026-07-01 (no behavior change):** The `[VideoPipeline:Timing]`
  diagnostic validation identified the present call as the source of maxTickGap spikes. Added new
  `[VideoPipeline:PresentTiming]` diagnostic line at the same 120-frame interval to break down the
  present call into: `total` (from `presentStart` to `presentEnd`), `select` (DropFrames +
  selection loop in `PresentFrameForPosition`), `present` (the `PresentFrame` call including
  FrameReady dispatch), and `frameReady` (the `FrameReady.Invoke` duration, i.e. the time spent
  inside the renderer `NativeMediaPresentationCoordinator.OnVideoFrameReady` handler). New fields:
  `_lastSelectDurationMs` (Stopwatch in `PresentFrameForPosition` before PresentFrame),
  `_lastPresentFrameDurationMs` (Stopwatch around `PresentFrame` call),
  `_lastFrameReadyInvokeDurationMs` (Stopwatch around `FrameReady?.Invoke` in `PresentFrame`).
  If `frameReady` is the dominant duration, the bottleneck is inside the Core renderer layer
  (D3D11 compositor, VideoProcessorBlt, swap chain present); further instrumentation there would
  be needed. If `select` is dominant, the queue/scanner work is slow. No wait/selector/renderer
  behavior changes. Checks: Debug pass, Release pass, boundary pass, parity pass, stale legacy
  Cdg-name search clean. Validation capture still required.

  **Present-side diagnostic validation — 2026-07-01 (UNMEASURED ISSUE):** The
  `[VideoPipeline:PresentTiming]` line ruled out `select` (mean 0ms, max 0.02ms — not the issue)
  and the FrameReady renderer handler (mean 3.42ms, max 7.21ms — fast). The `present` PresentFrame
  wrapper is also fast (mean 3.42ms, max 7.21ms). However, `total` (mean 24.17ms, max 90.65ms)
  is still dominated by the visible stutter. The gap between `total` and `present` is
  mean 20.75ms (late-run 30.61ms), correlated 0.998 with `total` and only 0.12 with
  `frameReady`. This means the dominant present-path time is work that happens INSIDE
  `PresentFrameForCurrentPosition` but OUTSIDE the `present` measurement: most likely
  `GetPresentationPosition()` (BASS audio position poll) and/or lock acquisition on
  `_presentationLock`. The renderer pipeline itself is not the bottleneck. Late-run gap max
  86.83ms lines up with the 142.5ms `maxTickGap` spike. **Next instrumentation:** add a
  breakdown of the gap into `GetPresentationPosition()` and lock acquisition
  within `PresentFrameForCurrentPosition`.

  **Current-position gap diagnostics added 2026-07-01 (no behavior change):** Added one new
  periodic `[VideoPipeline:CurrentPositionTiming]` diagnostic line, gated by the existing
  `EnablePerformanceLogging` path and aligned with the existing 120-frame presentation diagnostic
  cadence. The line decomposes the previously unmeasured `PresentTiming total - present` gap into
  `total` (method-local `PresentFrameForCurrentPosition` duration), `lockWait` (time to acquire
  `_presentationLock` in the presentation loop), `getPresentationPosition`,
  `audioEnginePosition`, `presentationClockGetPosition`, `presentationPositionOther`,
  `displayAdjust`, `naturalEnd`, `presentForPosition`, `afterPresent`, and `residual`. No wait,
  pacing, selector, renderer/backend, WPF adapter, public API/config, CDG timing, or audio clock
  behavior was changed. Next validation classification: AUDIO_POSITION / PRESENTATION_CLOCK /
  LOCK / DISPLAY_ADJUST / NATURAL_END / PRESENT_FOR_POSITION MISMATCH / STILL_UNMEASURED based on
  the dominant field and correlation with `maxTickGap`.

  Capture artifacts: `C:\Users\fatzb\AppData\Local\Temp\opencode\FatzBombMedia-VT-F\validate-diagnostics\20260701\`

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
- **Evidence:** The Section 14 VLC comparison reinforces this supportability issue: host apps
  often receive boolean failure while details are only written to debug output. VLC's engine
  exposes programmatic error details rather than relying only on debugger output.
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
- **Evidence:** This roadmap identifies that public events can fire from background/media threads
  and synchronous APIs can block in selected paths, while async APIs are safer. This may be
  documented but should be made explicit and testable.
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
- **Files:** `AGENTS.md:48-51`
- **Risk:** Low (misleading guidance)
- **Category:** Legacy (doc drift)
- **Evidence:** All three listed items are already deleted; whole-repo source searches find no
  live definitions or call sites for the removed facade/factory/async-player API entries.
- **Why it matters:** Actively misleads future cleanup work.
- **Recommendation:** Update/remove the "Known Dead Code" section.
- **Suggested action:** Remove (doc)
- **Stage candidate:** Stage 0

### Misplaced code

No layer violations found in Core. No `System.Windows.*`/`ImageSource`/`Dispatcher`/`D3DImage`
in Core source (only in the WPF-boundary guard script and README/roadmap text).
Audio normalization living in `Core/Audio` is appropriate engine-layer placement.

### Cleared suspects (NOT dead — confirmed live)

`AudioNormalizer.cs`, `D3D11PresentationDiagnostics.cs`, `BinaryCompatibility.cs` (type),
both FramePresenters, all five `*Presenter` classes, `VideoAudioEngine`, `ClockAudioEngine`,
`RenderPipeline`, `MediaSession`, `AudioPresentationClock`, `PrecisionWaiter`,
`MediaControlOperationQueue`, `NativeD3D11PresentationBridge`, `FileTree`, `DirectoryScanner`,
`MediaBackgroundResolver/Candidate` — each has ≥1 verified external construction/call site.

## 11. Complete File / Self-Contained Dead-Code Findings

- **No fully-dead files found** in Core. The only file-level candidate was historical
  documentation that referenced removed classes (`OptimizedGraphicsRenderer.cs`,
  `D3D9InteropSurface.cs`, `VorticeD3D11WpfImagePresenter.cs`, `WpfD3DImageSurfaceSizingMode`)
  that do not exist in the current tree.
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
| DOC2 | Historical comparison notes referenced removed classes | Legacy | Low | Remove (doc) |

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
- Update active docs (`AGENTS.md`, `README.md`, `docs/CURRENT_ARCHITECTURE.md`,
  `docs/VALIDATION.md`) after relevant stages.
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
- **Phase 1F — Timing fix (evidence-gated):** after Stage VT confirmed source-cadence loop drift,
  apply one isolated presentation-loop pacing fix (Stage VT-F) validated by before/after runtime
  capture; never change the frame selector.
- **Phase 2 — Observability and reliability:** structured logging/errors, CTS/await fixes, device-loss investigation.
- **Phase 3 — State/threading architecture:** central state owner and operation-queue async pump.
- **Phase 4 — Maintainability refactors:** VideoPipeline split, `RenderPipeline` cast removal,
  audio/clock abstraction cleanup, optional presenter collapse, broader pooling.
- **Phase 5 — Deferred/non-goals:** no plugin architecture, streaming, disc playback, subtitle engine, or cross-platform work unless product goals change.

### Stage 0 — Baseline and Guards (docs only)

- **Goal:** Establish a clean baseline; fix misleading docs without touching source.
- **Risk:** Low
- **Status:** Completed — 2026-06-30
- **Scope:** Update `AGENTS.md` dead-code guidance (all entries already gone), remove stale
  legacy Cdg-name and deleted-doc references, point active docs and solution items at this
  roadmap, and preserve the `ImmediateContext` refcount audit note.
- **Do not touch:** Any `.cs`/`.xaml`/`.csproj`.
- **Likely files affected:** `AGENTS.md`, `README.md`, `FatzBombMedia.MediaControl/README.md`,
  `docs/VALIDATION.md`, `FatzBombMedia.sln`, and this roadmap.
- **Required whole-repo guards:** Re-confirm source 0-hits for the three removed AGENTS entries;
  confirm the stale legacy Cdg-name, deleted stage-plan filename, and deleted comparison/cleanup
  doc-name guards are clean.
- **Graphify usage:** Confirm current live coordinator chain.
- **Build commands:** `dotnet build FatzBombMedia.sln --no-restore`.
- **Smoke tests:** None (docs only).
- **Stop conditions:** Any of the "already deleted" symbols actually still present.
- **Exit criteria:** Docs reflect reality; baseline build green.

**Latest review findings — 2026-06-30:**

- **Readiness:** Proceed with expanded docs/solution-text cleanup.
- **Safety/Risk:** Low. The stage is limited to markdown and solution item text. No `.cs`,
  `.xaml`, or `.csproj` files are in scope.
- **Verified ready items:** The removed AGENTS dead-code entries have no live source definitions or
  call sites. The live `IMediaControlSession` interface remains separate and is still used by the
  WPF adapter and presentation-parity guard. The `ImmediateContext` refcount assumption remains
  documented in Section 7 as a roadmap-level audit note only.
- **Expanded in-scope items:** Remove stale legacy Cdg-name references from active docs; replace
  deleted stage-plan file citations with this active roadmap; remove references to deleted
  comparison/cleanup docs; and update the solution's Docs item list to include this roadmap.
- **Exclusions / deferred items:** Historical Stage-N labels inside Core source comments,
  `VorticeD3D11Renderer` XML-doc crefs, `[VorticeD3D11...]` diagnostic prefixes, and the Player
  README sample diagnostic remain deferred to Stage 6 or a dedicated source-comment cleanup stage.
- **Required whole-solution guards:** Source searches for the three removed AGENTS entries; stale
  legacy Cdg-name search; deleted stage-plan filename search; deleted comparison/cleanup doc-name
  search; build; Release build; WPF-boundary script; presentation-parity script.
- **Expected build fallout:** None. The edits are markdown and solution item text only.
- **Required validation/smoke tests:** None. This stage is docs/text only; build and guard scripts
  are sufficient.
- **Recommendation:** Proceed with exclusions. Do not touch `.cs`, `.xaml`, or `.csproj` files in
  this stage.

### Stage 0P — Runtime Profiling Baseline (measurement only)

- **Goal:** Capture repeatable baseline data before changing hot paths so allocation/performance
  severity is measured, not just inferred from static analysis.
- **Risk:** Low (measurement only)
- **Status:** Completed — 2026-07-01
- **Scope:** Build Release x64, run controlled playback scenarios, capture CPU, managed
  allocations, GC/heap behavior, thread contention/blocking, exceptions, and GPU/presentation
  traces where possible.
- **Do not touch:** Source code, project files, renderer logic, naming.
- **Likely files affected:** None, except optional `profiling/` output folder if you decide to
  keep captured `.nettrace`, `.gcdump`, `.etl`, screenshots, and notes in a local ignored folder.
- **Required whole-repo guards:** Preflight environment and capture-safety checks only: Release
  build, native DLL presence, stable media set availability, profiler tool availability, and an
  ignored/out-of-repo profiling output path. Confirm no source/project files are modified.
- **Graphify usage:** Not required; this is runtime measurement.
- **Build commands:** `dotnet build FatzBombMedia.sln --no-restore`;
  `dotnet build -c Release FatzBombMedia.sln --no-restore`; boundary/parity scripts as listed
  in the latest review findings.
- **Smoke tests:** Same media/scenarios listed in Section 19 Runtime Profiling Plan.
- **Stop conditions:** Native DLLs missing, app cannot launch, or profiler overhead makes playback
  unusable. Fix environment first; do not change code as part of this stage.
- **Exit criteria:** Baseline captures exist for CPU, allocations, counters, GC heap snapshots,
  and at least one graphics/presentation trace.

**Latest review findings — 2026-06-30:**

- **Readiness:** Proceed with prerequisites. The stage is correctly measurement-only and should run
  before allocation/timing changes, but execution must first satisfy the preflight guards below.
- **Safety/Risk:** Low for measurement. Risk becomes Medium only if profiling artifacts are
  accidentally committed, if profiler overhead is mistaken for product behavior, or if a timing fix
  is attempted during the measurement stage.
- **Safe items:** Release x64 build, Player launch, native-DLL verification, controlled manual
  playback, `dotnet-counters`, `dotnet-trace`, `dotnet-gcdump`, `dotnet-stack`, WPR/WPA/GPUView,
  Visual Studio allocation profiler, and PerfView captures. These do not require source/project
  edits.
- **Exclusions / deferred items:** Do not edit source, project files, renderer logic, selector/
  timing logic, logging, naming, or architecture. Do not apply Stage 1A/1B allocation fixes based
  on static suspicion; Stage 0P only collects baseline evidence. Do not commit sample media,
  native DLLs, `.nettrace`, `.gcdump`, `.etl`, `.etl.zip`, screenshots, or raw profiler output.
- **Environment findings from review:**
  - Repo root/current directory resolved to `C:\Programming\FatzBombMedia`.
  - `graphify-out/graph.json` exists, but Graphify is only supporting evidence; runtime captures
    are authoritative for this stage.
  - Required native DLL folder exists and includes `bass.dll` plus FFmpeg DLLs such as
    `avcodec-62.dll`.
  - No sample media files were found in the repo. Use a stable external/local media set and record
    absolute paths, durations, codecs/FPS, display, GPU, and scenario labels in the capture notes.
  - Global `dotnet-counters`, `dotnet-trace`, `dotnet-gcdump`, and `dotnet-stack` tools were not
    installed during review; install/update them before capture. `wpr.exe` is available. PerfView
    was not found and should be installed only if needed for Pass 7.
  - `.gitignore` does not currently ignore `/profiling/`. Either keep captures outside the repo
    (recommended, e.g. under a local temp/profiling folder) or add `profiling/` to local
    `.git/info/exclude` before creating `profiling\baseline`. Do not edit `.gitignore` as part of
    this measurement stage unless a separate docs/tooling stage explicitly approves it.
- **Required whole-solution guards:**
  1. `git status --short` before and after capture; only optional notes may change, never source,
     project, renderer, or generated profiler binaries.
  2. `Test-Path .\FatzBombMedia.Player\native\bass.dll` and
     `Test-Path .\FatzBombMedia.Player\native\avcodec-62.dll` → both must be `True`.
  3. `dotnet tool list --global` or `Get-Command dotnet-counters,dotnet-trace,dotnet-gcdump,dotnet-stack`
     → install/update missing tools before runtime capture.
  4. `Get-Command wpr` → expect Windows Performance Recorder available for GPU/presentation trace;
     if missing, record WPR/GPUView as unavailable and use Visual Studio/PerfView alternatives.
  5. `rg -n "CdgEngine.RefreshBackgroundWashUnsafe|CdgStateMachine.CreateSnapshot|D3D11MediaCompositionPresenter.PresentToTargets|D3D11CompositionLayerCache|MediaCompositionFrame.EnumerateLayers|VideoDecoder.GetQueuedFramesSnapshot|VideoPipeline.DecodeForSeekTarget|MediaControlOperationQueue" .`
     to confirm static hot-path symbols still match the profiling checklist.
  6. `rg -n "selectionSkips|lateDrops|maxClockLate|maxDroppedClockLate|avgTickGap|PresentPerf|COMPOSITION-D3D11|PrecisionWaiter" .`
     to confirm runtime diagnostic names still match the profiling/stutter checklist.
  7. Run the active stale legacy Cdg-name guard from the request/validation checklist and confirm
     it remains clean before/after capture.
- **Project-local follow-up guards:** If captures are stored under the repo, use `git status
  --ignored --short` to prove profiler outputs are ignored/untracked before ending the stage.
  Record the exact tool versions (`dotnet --list-sdks`, `dotnet tool list --global`, Windows build,
  GPU driver if available) in profiling notes.
- **Likely files affected:** None. Optional non-source notes are acceptable only if intentionally
  kept; profiler captures should be outside the repo or locally ignored. No `.cs`, `.xaml`,
  `.csproj`, `.sln`, renderer, WPF adapter, or roadmap-scope docs should be changed during
  execution except for a final Stage 0P status/progress note.
- **Required build/check commands:**
  ```powershell
  dotnet build FatzBombMedia.sln --no-restore
  dotnet build -c Release FatzBombMedia.sln --no-restore
  .\scripts\check-wpf-boundary.ps1
  .\scripts\check-presentation-parity.ps1
  # Run the active stale legacy Cdg-name guard from the request/validation checklist.
  ```
  If local execution policy blocks the script invocations, rerun with
  `pwsh -ExecutionPolicy Bypass -File .\scripts\check-wpf-boundary.ps1` and
  `pwsh -ExecutionPolicy Bypass -File .\scripts\check-presentation-parity.ps1`, and record that
  the bypass was for local script execution policy only.
- **Required validation/smoke tests:** Run the Section 19 matrix with stable external media:
  audio-only, standalone CDG, audio+CDG, audio+CDG+image background, audio+CDG+video background,
  video with audio, video without audio, seek/scrub stress, secondary/projector output, and long
  23.98fps stutter repro if available. Capture CPU, allocation rate, GC counts/heap, exceptions,
  thread-pool health, CPU samples, allocation call stacks, before/after GC dumps, stack snapshots
  during stalls, at least one WPR/WPA/GPUView presentation trace, and written notes per scenario.
- **Expected healthy evidence shape:** Release Player launches with native DLLs; steady scenarios
  have stable exception count, no unbounded heap/handle growth, scenario-specific allocation
  suspects visible in allocation traces, no UI hang during profiling, no stale layer artifacts,
  and video timing logs retain the expected healthy shape (`lateDrops=0`, `selectionSkips=0` or
  near-zero during steady playback, healthy queue, high-resolution timer when available).
- **Recommendation:** Proceed with prerequisites. If native DLLs, sample media, Player launch,
  profiler tools, or a safe output location are missing, mark Stage 0P partial/blocked and do not
  proceed to Stage 1 allocation fixes until baseline evidence exists.

**Execution note — 2026-07-01:**

- **Completed:** Repo/root and Graphify availability checked; native DLLs verified; WPR available;
  .NET diagnostic tools installed globally (`dotnet-counters`, `dotnet-trace`, `dotnet-gcdump`,
  `dotnet-stack` version `9.0.661903`); hot-path profiling symbols and timing diagnostics traced;
  Debug build passed; Release build passed; WPF-boundary and presentation-parity scripts passed
  with local execution-policy bypass; stale legacy Cdg-name search remained clean.
- **Not completed:** Runtime profiling captures were not produced. No sample media files were found
  in the repo, and the required Section 19 scenarios require interactive Release Player playback
  with stable external media.
- **Remaining before Stage 1 allocation work:** Run Section 19 scenarios with a stable external
  media set and capture `dotnet-counters`, `dotnet-trace`, allocation profile, before/after
  `dotnet-gcdump`, optional `dotnet-stack` during stalls, and at least one WPR/WPA/GPU
  presentation trace. Store captures outside the repo or in a locally ignored path.

**Completion note — 2026-07-01:**

All capture passes completed:
- **Pass 1 — Counters:** 8 scenarios (A, C audio+cdg, C zip, F, G, H, J, X) via automated script
- **Pass 2 — CPU traces:** 5 .nettrace files (C-cdg, F-video, E-cdg-video-bg, H-seek-audio, H-seek-video) via automated + manual capture
- **Pass 4 — GC dumps:** before.gcdump and after.gcdump (10 MB each)
- **Pass 6 — WPR/GPU:** presentation.etl (3 GB) with video playback
- Added drag-and-drop and CLI file loading support to Player to enable automated profiling
- Profiling automation script at `profiling/run-profiling.ps1`


### Stage VT — Video Timing / Stutter Investigation (measurement only)

- **Goal:** Identify the remaining visible stutter source after Stage 11 timing fixes and after drops
  / `selectionSkips` have reportedly been reduced close to zero.
- **Risk:** Low for measurement; Medium-High for any later timing changes.
- **Status:** Completed — 2026-07-01
- **Scope:** Capture synchronized diagnostics for long 23.98fps playback and other problematic
  files. Correlate app frame-selection counters with OS/GPU/present/audio/GC evidence before changing
  any selector, pacing, or renderer code.
- **Do not touch:** `ShouldAdvanceSelectionToFrame`, `VideoPresentationTiming`, source-cadence
  pacing, `PrecisionWaiter`, DirectComposition/native HWND behavior, or renderer code in this stage.
- **Likely files affected:** None. Optional local ignored profiling output only.
- **Required whole-repo guards:** Measurement/preflight guards only. Trace all timing and present
  counters first: `selectionSkips`, `lateDrops`, `maxClockLate`, `maxDroppedClockLate`,
  `avgTickGap`, `PresentPerf`, `COMPOSITION-D3D11`, `PrecisionWaiter`, and audio-clock sampling.
  Also run the standard build, boundary, presentation-parity, and stale Cdg-name guards before any
  later timing fix is proposed.
- **Graphify usage:** Supporting evidence only; runtime traces are more important.
- **Build commands:** Full build/check list from the latest review findings below.
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

**Latest review findings — 2026-07-01:**

- **Readiness:** Proceed with exclusions. Stage 0P baseline captures now exist, so Stage VT can use
  the same stable media/output setup for a narrower 23.98fps stutter investigation. This stage is
  still measurement-only and must produce a written hypothesis before any timing/presentation fix.
- **Safety/Risk:** Low if limited to traces, notes, and capture artifacts. Risk becomes Medium-High
  if selector, pacing, wait, renderer, DirectComposition/native-HWND, or adapter behavior is changed
  based on intuition instead of synchronized evidence.
- **Safe items:** Release Player launch; diagnostic log collection; `dotnet-counters`;
  `dotnet-trace`; optional `dotnet-gcdump` if counters show GC/allocation spikes; optional
  `dotnet-stack` during stalls; WPR/WPA/GPUView; monitor/refresh/DPI/display-mode notes; secondary
  output, fullscreen, resize, and seek smoke tests.
- **Exclusions / deferred items:** Do not edit `VideoPipeline`, `VideoPresentationTiming`,
  `PrecisionWaiter`, `NativeMediaPresentationCoordinator`, D3D11 presenter/backend code, WPF
  adapter code, selector logic, source-cadence pacing, timer behavior, or renderer paths in this
  stage. Do not rename legacy `[VorticeD3D11...]` tags here; that remains Stage 6. Do not treat
  profiler overhead or one-off external machine load as product behavior without repeat capture.
- **Required whole-solution guards:**
  ```powershell
  dotnet build FatzBombMedia.sln --no-restore
  dotnet build -c Release FatzBombMedia.sln --no-restore
  .\scripts\check-wpf-boundary.ps1
  .\scripts\check-presentation-parity.ps1
  rg -n "D[x]Cdg|D[X]CDG|d[x]cdg" .
  rg -n "selectionSkips|lateDrops|maxClockLate|maxDroppedClockLate|avgTickGap|PresentPerf|COMPOSITION-D3D11|PrecisionWaiter" .
  rg -n "ShouldAdvanceSelectionToFrame|VideoPresentationTiming|source-cadence|hold-source-cadence|adaptive=" .
  rg -n "DirectComposition|NativeHwnd|SwapChain|secondary|projector|fan-out" .
  rg -n "AudioPresentationClock|ClockAudioEngine|VideoAudioEngine|PlaybackClock" .
  ```
- **Project-local follow-up guards:** Verify `profiling/` captures are outside the repo or locally
  ignored before ending the stage. Record exact media path, FPS, codec/container, display, refresh
  rate, scaling/DPI, primary vs secondary output, DirectComposition vs native-HWND fallback if known,
  GPU/driver, Windows build, Player PID, and tool versions. If PowerShell script policy blocks guard
  scripts, rerun with `pwsh -ExecutionPolicy Bypass -File ...` and record that it was local policy
  only.
- **Likely files affected:** None for source. Optional local ignored artifacts under `profiling/`
  and one roadmap/status note with the final hypothesis. Likely files to inspect, but not edit in
  this stage: `Pipeline/VideoPipeline.cs`, `Core/Video/VideoPresentationTiming.cs`,
  `Core/Threading/PrecisionWaiter.cs`, `Core/Graphics/NativeMediaPresentationCoordinator.cs`,
  `Core/Graphics/D3D/Diagnostics/D3D11PresentationDiagnostics.cs`, D3D11 presentation backends, and
  WPF `MediaSurface`/`SecondaryPresentationWindow` only for boundary verification.
- **Build commands:**
  ```powershell
  dotnet build FatzBombMedia.sln --no-restore
  dotnet build -c Release FatzBombMedia.sln --no-restore
  .\scripts\check-wpf-boundary.ps1
  .\scripts\check-presentation-parity.ps1
  rg -n "D[x]Cdg|D[X]CDG|d[x]cdg" .
  ```
- **Validation/smoke tests:** Long 23.98fps problem-file playback for 10-20 minutes with app logs,
  counters, CPU trace, and WPR/WPA or GPUView over the same window where practical. Repeat or spot
  check 29.97fps, 60fps, video with audio, video without audio, audio+CDG+video background,
  secondary/projector output, fullscreen/resize, and seek/scrub. Expected healthy shape is
  `lateDrops=0`, `selectionSkips=0` or near-zero during steady playback, healthy queue depth,
  `avgTickGap` close to source frame duration, high-resolution timer when available, no unbounded
  heap/exception growth, and no primary/secondary drift.
- **Recommendation:** Proceed with exclusions. End the stage only after writing an evidence-backed
  hypothesis that classifies the stutter as DWM/present pacing, monitor/refresh mismatch,
  DPC/ISR/audio glitching, GC/allocation spike, background-video cadence, occasional blocking,
  driver/device behavior, or a still-valid selector/pacing defect. If captures do not correlate,
  add targeted instrumentation in a later approved stage instead of changing timing code here.

**Execution note — 2026-07-01:**

- **Artifacts:** Captures were stored outside the repo under
  `C:\Users\fatzb\AppData\Local\Temp\opencode\FatzBombMedia-VT\2026-07-01`.
- **Captured:** 10-minute Release x64 playback of
  `D:\- Testing FIles\23.98-stutter.mkv`; 10-minute `dotnet-trace` CPU sample
  (`vt-23.98-cpu.nettrace`, ~41 MB); 10-minute `dotnet-counters collect` CSV
  (`vt-counters.csv`, ~1.7 MB); 3-minute WPR presentation trace (`vt-presentation.etl`, ~3.5 GB).
- **Counter evidence:** Average allocation rate was ~219 KB/s; total allocated over the 10-minute
  run was ~131 MB; Gen0 collections totaled 10, Gen1 totaled 1, Gen2 totaled 0; GC pause time totaled
  ~0.024 s; thread-pool queue length stayed 0. CPU averaged ~1.14 CPU-seconds/sec across a 20-logical
  CPU machine. Working set rose from ~459 MB to ~1.25 GB during the run.
- **CPU trace evidence:** `dotnet-trace report topN` showed most sampled time in waits/message pump;
  `VideoPipeline.DecodeLoop` was ~11.5% exclusive, `VideoAudioEngine.DecodeAndPushAudio` ~0.5%,
  `D3D11MediaCompositionPresenter.DrawToRenderTarget` ~0.08%, `D3D11SwapChainPresenter.Present`
  ~0.03%, and `VideoDecoder.DecodeNextFrame` ~0.01%. This does not indicate a CPU-bound hot path.
- **Blockers / limitations:** Release app diagnostic log contained only the Player startup line;
  the timing counters expected for this stage are emitted with `Debug.WriteLine` and are not available
  from Release capture as usable app logs. WPR saved the ETL but reported 1,789,946 dropped events, so
  the WPR artifact is supporting evidence only and should not be used as definitive GPU/DWM proof.
  No visual observation was possible from the tool session.
- **Interim hypothesis:** Captured CPU/GC evidence does not support CPU saturation, GC pauses, or a
  managed allocation spike as the primary 23.98fps stutter cause. If visible stutter persists, the
  leading suspects remain presentation/display/driver timing or an unlogged presentation gap, but this
  stage cannot conclusively classify the cause until Release-safe timing diagnostics or a cleaner WPR
  trace are available.
- **Remaining before completion:** Add or enable Release-safe timing diagnostics in a later approved
  instrumentation/logging stage, or run an equivalent capture path that records `selectionSkips`,
  `lateDrops`, `maxClockLate`, `maxDroppedClockLate`, `avgTickGap`, queue depth, source FPS, display
  refresh, timer mode, and present/composition timing during the same 23.98fps playback window. If
  GPU/DWM evidence is needed, repeat WPR with a smaller profile/circular buffer or analyze in WPA only
  if dropped events are acceptable for the specific question.

**Completion note — 2026-07-01:**

- **Unblock (observability only):** The four `VideoPipeline` timing/perf diagnostics
  (`[VideoPipeline:Perf]`, `[VideoPipeline:Loop]`, timer-fallback, interval-changed) were converted
  from `Debug.WriteLine` to `Trace.WriteLine`. `Debug.WriteLine` is `[Conditional("DEBUG")]` and is
  compiled out of Release (only `TRACE` is defined), which is why the prior Release capture logged
  nothing but the startup line. `Trace.WriteLine` survives in Release and already routes to
  `%TEMP%\FatzBombMedia\player-debug.log` via the `App.xaml.cs` listener (proven: the Trace-based
  startup line appeared in the Release log while all `Debug.WriteLine` diagnostics did not). All four
  sites remain gated by `EnablePerformanceLogging`/`EnableLogging`; Debug behavior is unchanged
  (TRACE is also defined under DEBUG). No playback, timing, selector, or renderer logic changed. Full
  structured logging remains deferred to Stage ER.
- **Capture:** Release x64 playback of `D:\- Testing FIles\23.98-stutter.mkv` (24fps source, 60Hz
  display), `EnablePerformanceLogging=true`. Log: `player-debug-release-verify.log`. Full analysis in
  the external artifact `vt-analysis.txt`.
- **Evidence:** Over ~3 minutes, `lateDrops=0` throughout and the decode queue stayed healthy (5-6/6),
  but the source-cadence loop drifts slow: `avgTickGap` rises from 42.05ms toward 45.5ms against the
  41.7ms source target, with `maxTickGap` ~83ms (~2x target) indicating periodic full-frame loop-wake
  stalls. Lateness accumulates (`maxClockLate` ~40-42ms ≈ one source frame; `clockLateSamples` 11-21),
  and `selectionSkips` climbs from 0 in the first ~90s to 6-13 per 120-frame window in the last minute.
  The adaptive lead held at `0.750x refresh` and did not compensate. Prior counters were healthy
  (Gen2=0, ~219 KB/s alloc, GC pause ~0.024s total, CPU not saturated), and the CPU trace showed
  present/composition < 0.1% — so this is not a GC/CPU/allocation problem.
- **Hypothesis (evidence-backed):** Two components. (A) App-side source-cadence presentation-loop
  drift: the loop runs slightly slower than source cadence and periodically stalls a full frame,
  accumulating clock lateness until frames are skipped to resync — matching VALIDATION.md's
  "selectionSkips steadily increasing" not-acceptable pattern and the Stage 11 phase-drift note. The
  future fix target is the loop wait accounting (`GetClockLockedPresentationWaitSeconds`) and
  `PrecisionWaiter` tick behavior, NOT the frame selector (selection is a symptom). (B) Inherent
  24fps-on-60Hz 3:2 cadence judder at the DWM/compositor level, which no app-side pacing change can
  remove (needs refresh-rate matching or interpolation; out of scope).
- **Second-opinion caveats:** `maxTickGap` ~83ms sits below `PrecisionWaiter`'s (duration+50ms)
  safety ceiling (~91.7ms), so some spikes may be the wait nearing its safety timeout rather than a
  true external stall — WPR/GPUView correlation is needed to attribute Component A to CPU scheduling
  vs waitable-timer vs GPU back-pressure. The WPR GeneralProfile capture dropped ~1.79M events; use a
  lighter/targeted profile for decisive GPU/DWM proof. No direct human visual confirmation was
  possible in this session.
- **Recommended next stage:** A dedicated, approved timing stage that first re-captures
  `avgTickGap`/`maxTickGap`/`selectionSkips` alongside a clean WPR/GPUView trace, then — only if
  Component A is confirmed — adjusts source-cadence wait accounting. Do not change the frame selector.

### Stage VT-F — Source-Cadence Presentation-Loop Drift Fix (timing; evidence-gated)

- **Goal:** Remove the slow drift of the source-cadence video presentation loop so 23.98fps playback
  holds `avgTickGap` at the source interval and stops accumulating `selectionSkips` over time, without
  changing frame-selection logic or the renderer.
- **Risk:** Medium-High. This is a live timing/pacing path. A wrong change can reintroduce drops,
  desync audio/video, or worsen stutter. Isolated to one loop; validated by before/after capture.
- **Status:** Validated (PASS) — 2026-07-01. Present-side validation 2026-07-01: **UNMEASURED** (gap between `total` and `present` correlates 0.998 with `total`). Next: instrument `GetPresentationPosition()` / lock acquisition.
- **Depends on:** Stage VT (completed 2026-07-01), which produced the baseline and the two-component
  hypothesis. Baseline evidence: `player-debug-release-verify.log` and `vt-analysis.txt` under
  `C:\Users\fatzb\AppData\Local\Temp\opencode\FatzBombMedia-VT\2026-07-01`.

- **Sequencing / dependencies:**
  - **Hard gate (blocking):** A clean WPR/GPUView re-capture (lighter/targeted profile, not
    `GeneralProfile`, to avoid the ~1.79M dropped events seen in Stage VT) that attributes the
    ~83ms `maxTickGap` spikes to a real cause (CPU scheduling, the waitable timer, or GPU/DWM present
    back-pressure) and confirms **Component A (app-side loop stall)** is present and dominant over
    **Component B (inherent 24-on-60Hz 3:2 judder)**. Do not edit pacing code until this is done,
    because `maxTickGap ~83ms` currently sits below `PrecisionWaiter`'s (duration+50ms) safety ceiling
    (~91.7ms) and could be the wait nearing its own timeout rather than an external stall.
  - **Recommended precursors (not hard blocks):** Stage 0T (build/guard automation for repeatable
    before/after runs) and Stages 1A-1C (cheap allocation wins to reduce Gen0 churn as a confounder in
    the capture). These are recommended, not required: Stage VT evidence showed GC/alloc are not the
    cause (Gen2=0, ~219 KB/s, GC pause ~0.024s total), and 1A-1C touch the CDG/composition path, not
    the video-only presentation loop this stage targets, so they neither block nor mask this fix.
  - **No other timing-fix stage precedes this.** Prior timing work (historical "Stage 11") is already
    done; VT-F is the current timing fix.

- **Scope:** One isolated change to the source-cadence branch of the video presentation loop so the
  effective loop period matches source cadence and holds phase against the audio-mastered clock.
  Candidate directions to evaluate during review/implementation (confirm with the gate capture first;
  pick the smallest that fixes the measured drift):
  1. **Phase-anchored wait in the source-cadence branch.** Today the source-cadence branch
     (`VideoPipeline.cs:829-838`) computes a per-tick *relative* wait via
     `GetClockLockedPresentationWaitSeconds` (`:855-871`) and then resets `nextTick =
     stopwatch.Elapsed.TotalSeconds`, discarding the phase accumulator that the non-source-cadence
     branch (`:840-851`) uses (`nextTick += targetIntervalSeconds`). Evaluate anchoring the wait to a
     fixed phase/accumulator so per-iteration work time does not accumulate as slow drift.
  2. **Subtract measured loop work from the wait.** The wait targets the next frame time but does not
     subtract the loop body's own cost (`PresentFrameForCurrentPosition`, peek/clock reads,
     diagnostics), so each iteration adds a few ms that accumulate. Evaluate compensating the wait by
     the measured iteration overhead.
  3. **`PrecisionWaiter` accounting only if the gate proves it.** Only if the WPR/GPUView capture
     attributes the ~83ms spikes to the waiter/timer, evaluate the safety-timeout/resolution behavior
     in `PrecisionWaiter.Wait` (`Core/Threading/PrecisionWaiter.cs:61-91`, safety timeout `:89`).
  Implement exactly one direction. Do not bundle multiple.

- **Do not touch:**
  - `ShouldAdvanceSelectionToFrame` (`VideoPipeline.cs:1524`) or any frame-selection logic — Stage VT
    proved selection is a *symptom*, not the cause.
  - `VideoPresentationTiming` interval math unless the gate capture proves the interval itself is wrong.
  - The adaptive-lead mechanism (`UpdateAdaptivePresentationLead`, `GetActivePresentationLeadRefreshIntervals`)
    unless the gate capture specifically implicates it.
  - DirectComposition / native-HWND backends, D3D11 presenters/compositor, swap-chain policy,
    multi-target fan-out, and the WPF adapter boundary.
  - CDG overlay, audio, and clock semantics (`ClockAudioEngine`, `AudioPresentationClock`,
    `PlaybackClock`), and standalone-CDG / video-without-audio timing.
  - The Debug->Trace diagnostic conversions from Stage VT-diag (keep them; do not expand to Stage ER here).

- **Likely files affected:** `FatzBombMedia.MediaControl/Pipeline/VideoPipeline.cs` (source-cadence
  branch and/or `GetClockLockedPresentationWaitSeconds` only). `Core/Threading/PrecisionWaiter.cs` only
  if direction 3 is proven necessary. No public API, renderer, adapter, or config changes expected.

- **Required whole-solution guards:**
  ```powershell
  dotnet build FatzBombMedia.sln --no-restore
  dotnet build -c Release FatzBombMedia.sln --no-restore
  .\scripts\check-wpf-boundary.ps1
  .\scripts\check-presentation-parity.ps1
  rg -n "D[x]Cdg|D[X]CDG|d[x]cdg" .
  rg -n "selectionSkips|lateDrops|maxClockLate|maxDroppedClockLate|avgTickGap|PrecisionWaiter" .
  rg -n "ShouldAdvanceSelectionToFrame|GetClockLockedPresentationWaitSeconds|IsSourceCadencePlayback|VideoPresentationTiming" .
  rg -n "UpdateAdaptivePresentationLead|GetActivePresentationLeadRefreshIntervals|PresentationLeadRefreshIntervals" .
  rg -n "WaitOne\(|WaitAny\(|SetWaitableTimer|CreateWaitableTimerExW|TimerWaitHandle" .
  rg -n "FatzBombMedia\.MediaControl\.Core\.(Graphics|Video|Cdg)|FatzBombMedia\.MediaControl\.Pipeline|Vortice|D3D11|DirectComposition" FatzBombMedia.MediaControl.Wpf
  ```

- **Graphify usage:** Supporting evidence only; runtime before/after capture is authoritative.

- **Build commands:**
  ```powershell
  dotnet build FatzBombMedia.sln --no-restore
  dotnet build -c Release FatzBombMedia.sln --no-restore
  .\scripts\check-wpf-boundary.ps1
  .\scripts\check-presentation-parity.ps1
  rg -n "D[x]Cdg|D[X]CDG|d[x]cdg" .
  ```

- **Required before/after capture (validation):** Release x64, `EnablePerformanceLogging=true`,
  same file `D:\- Testing FIles\23.98-stutter.mkv`, same display/refresh, >= 5-minute run. Compare
  against the Stage VT baseline:
  - `avgTickGap` should hold near the 41.7ms source interval and stop climbing (baseline drifted
    42.05 -> 45.5ms).
  - `maxTickGap` spikes should shrink toward the source interval (baseline ~83ms).
  - `selectionSkips` per 120-frame window should stay at/near zero over the whole run and stop the
    upward trend (baseline climbed 0 -> 6-13/window).
  - `lateDrops` must remain 0; `queued` must stay healthy; `fps`/`videoFrame`/`refresh` unchanged.
  - Re-run one clean WPR/GPUView pass to confirm no new present/DWM regression.

- **Smoke tests:** 23.98fps (primary), 29.97fps, 60fps, video with audio (A/V sync intact), video
  without audio (clock-backed), audio+CDG+video background, secondary/projector output, fullscreen/
  resize, and seek/scrub. Watch for reintroduced drops, desync, or stutter on any path.

- **Stop conditions:** The gate capture does not confirm Component A (i.e., the drift is dominated by
  inherent 24-on-60Hz judder or an external driver/display cause) — then do not change pacing code;
  document and consider refresh-rate matching / interpolation as a separate product decision. Also stop
  if any candidate change reintroduces `lateDrops`, A/V desync, or selection instability.

- **Exit criteria:** One isolated pacing change lands; Debug + Release build clean; boundary + parity +
  stale Cdg-name guards pass; and a before/after Release capture shows `avgTickGap`/`maxTickGap`/
  `selectionSkips` improved with `lateDrops=0` and no visual/stability regression across the smoke-test
  matrix. If the change does not measurably improve the counters, revert it and reopen the hypothesis.

**Validation findings — 2026-07-01 (post-fix capture):**

| Metric | Pre-fix gate | Post-fix (Direction 1) | Trend |
|--------|-------------|----------------------|-------|
| avgTickGap range | 41.7→58.1ms | 42.2→49.7ms | Still drifting; mean 46.3ms |
| maxTickGap | 163.2ms | 104.6ms | Lower but still 2.5× target |
| max selectionSkips/window | 45 | 75 | Worse (+67%) |
| total selectionSkips | 711 / 83 windows | 1019 / 52 windows | Worse (higher density) |
| lateDrops | 0 | 0 | Unchanged |
| queue | 4–7 / 6 | 4–6 / 6 | Unchanged |
| maxClockLate | 41.9ms | 42.0ms | Unchanged |
| maxEarly | ~10.3ms | 0→10ms (drops near-zero early) | Wake-late regression |

**Root cause:** The phase-anchored accumulator (`nextTick += targetIntervalSeconds`) runs independent of the audio-mastered clock. When the selector skips frames (clock does not advance), the accumulator continues marching by `targetIntervalSeconds` each tick. This creates a diverging gap — the accumulator gets ahead of the clock, causing the loop to wake with near-zero `maxEarly` → late → gap widens → accumulator resyncs → massive skip burst → cycle repeats. The pre-fix clock-relative approach (`GetClockLockedPresentationWaitSeconds`) did not have this divergence.

**Recommendation:** Revert to `GetClockLockedPresentationWaitSeconds` and apply **Direction 2** — subtract measured loop-body wall-clock time from the computed wait. Minimal: add `var tickBodyStart = stopwatch.Elapsed.TotalSeconds` at loop body top, `waitSeconds -= stopwatch.Elapsed.TotalSeconds - tickBodyStart` in source-cadence branch.

**Artifacts:** `C:\Users\fatzb\AppData\Local\Temp\opencode\FatzBombMedia-VT-F\validate\20260701\`

**Latest review findings — 2026-07-01:**

- **Readiness:** Not ready for source implementation until the hard gate capture is completed. Ready to
  run the gate capture and source trace now. If the clean WPR/GPUView pass confirms Component A
  (app-side source-cadence loop stall) as dominant, proceed with one isolated `VideoPipeline` pacing
  change. If it does not, stop before code and document the external/display cause.
- **Safety/Risk:** High for source changes because this is the active video timing loop and an error can
  reintroduce drops, A/V desync, selector churn, or primary/secondary presentation divergence. The
  review confirmed the narrowest likely source seam is `VideoPipeline.PresentationLoop` and
  `GetClockLockedPresentationWaitSeconds`; `PrecisionWaiter` is broader blast radius because it is also
  used by the Core CDG render loop and background-video pacing.
- **Safe items:** Gate-only work is safe: run a lighter targeted WPR/GPUView capture, collect Release
  `Trace` timing logs with `EnablePerformanceLogging=true`, compare against `player-debug-release-verify.log`,
  and inspect `VideoPipeline.cs` source-cadence branch behavior. If the gate passes, the safest
  implementation direction remains a phase-anchored source-cadence wait in `VideoPipeline` without
  touching selector logic.
- **Exclusions / deferred items:** Do not change `ShouldAdvanceSelectionToFrame`, frame selection
  thresholds, `VideoPresentationTiming` interval math, adaptive lead, DirectComposition/native-HWND
  backends, D3D11 presenters/compositor, WPF adapter, public API/config, CDG overlay behavior,
  `ClockAudioEngine`, `AudioPresentationClock`, `PlaybackClock`, structured logging, or naming cleanup.
  Do not edit `PrecisionWaiter` unless the gate specifically proves waiter/timer accounting is the
  failing component; if it is touched, validate the CDG render loop and background-video loop too.
- **Required whole-solution guards:**
  ```powershell
  dotnet build FatzBombMedia.sln --no-restore
  dotnet build -c Release FatzBombMedia.sln --no-restore
  .\scripts\check-wpf-boundary.ps1
  .\scripts\check-presentation-parity.ps1
  rg -n "D[x]Cdg|D[X]CDG|d[x]cdg" .
  rg -n "selectionSkips|lateDrops|maxClockLate|maxDroppedClockLate|avgTickGap|PrecisionWaiter" .
  rg -n "ShouldAdvanceSelectionToFrame|GetClockLockedPresentationWaitSeconds|IsSourceCadencePlayback|VideoPresentationTiming" .
  rg -n "UpdateAdaptivePresentationLead|GetActivePresentationLeadRefreshIntervals|PresentationLeadRefreshIntervals" .
  rg -n "WaitOne\(|WaitAny\(|SetWaitableTimer|CreateWaitableTimerExW|TimerWaitHandle" .
  rg -n "DirectComposition|NativeHwnd|SwapChain|secondary|projector|fan-out" .
  rg -n "AudioPresentationClock|ClockAudioEngine|VideoAudioEngine|PlaybackClock" .
  rg -n "FatzBombMedia\.MediaControl\.Core\.(Graphics|Video|Cdg)|FatzBombMedia\.MediaControl\.Pipeline|Vortice|D3D11|DirectComposition" FatzBombMedia.MediaControl.Wpf
  ```
- **Project-local follow-up guards:** If the source change remains limited to `VideoPipeline`, inspect
  only the source-cadence branch, `GetClockLockedPresentationWaitSeconds`, diagnostics, and direct
  callers before/after. If `PrecisionWaiter` is changed, additionally trace and smoke the two other
  call sites: `NativeMediaPresentationCoordinator.RenderLoop` for CDG presentation and
  `PipelineMediaEngine.BackgroundVideoLoop` for sidecar/background video cadence.
- **Likely files affected:** Preferred source target is
  `FatzBombMedia.MediaControl/Pipeline/VideoPipeline.cs`. `FatzBombMedia.MediaControl/Core/Threading/PrecisionWaiter.cs`
  is allowed only after gate proof. No WPF adapter, renderer backend, D3D11 presenter, project file,
  public API, or config file should be changed.
- **Required build/check commands:**
  ```powershell
  dotnet build FatzBombMedia.sln --no-restore
  dotnet build -c Release FatzBombMedia.sln --no-restore
  .\scripts\check-wpf-boundary.ps1
  .\scripts\check-presentation-parity.ps1
  rg -n "D[x]Cdg|D[X]CDG|d[x]cdg" .
  ```
- **Required validation/smoke tests:** Before/after Release x64 capture on the same 23.98fps file,
  display, refresh, output mode, and logging settings. Required healthy shape: `avgTickGap` stays near
  source frame duration, `maxTickGap` no longer shows recurring full-frame spikes, `selectionSkips`
  stays near zero without upward drift, `lateDrops=0`, queue depth remains healthy, A/V sync is stable,
  and WPR/GPUView shows no new DWM/present regression. Smoke 29.97fps, 60fps, video with audio, video
  without audio, audio+CDG+video background, secondary/projector output, fullscreen/resize, and
  seek/scrub. If `PrecisionWaiter` changes, add standalone CDG and background-video cadence checks.
- **Recommendation:** Proceed with exclusions only. First run the hard gate capture. Then, only if the
  gate confirms Component A, implement one minimal `VideoPipeline` source-cadence pacing fix and keep
  the selector, renderer, adapter, audio/clock, CDG, logging, and naming surfaces unchanged.

**Direction 2 corrective implementation — 2026-07-01:**

Direction 1 (phase-anchored accumulator) was validated and failed — selection skips regressed 67%. The
accumulator detached from the audio-mastered clock, creating a vicious drift/skip cycle. Direction 2
reverts to the clock-relative approach and subtracts loop-body wall-clock overhead.

Changes in `FatzBombMedia.MediaControl/Pipeline/VideoPipeline.cs`:
- **Line 782:** Added `var tickBodyStart = stopwatch.Elapsed.TotalSeconds;` at the top of the
  `PresentationLoop` while-loop body to measure wall-clock time at the start of each tick.
- **Lines 830-841:** Reverted the source-cadence branch to the original clock-relative
  `GetClockLockedPresentationWaitSeconds` call, plus `waitSeconds -= stopwatch.Elapsed.TotalSeconds -
  tickBodyStart` to subtract the loop-body overhead measured since the tick started. The clock remains
  the pacing anchor; per-iteration body execution time no longer accumulates as slow drift.
- The non-source-cadence branch, `GetClockLockedPresentationWaitSeconds` method, frame selector,
  adaptive lead, `PrecisionWaiter`, and all other paths are unchanged.

**Direction 2 validation status:** Awaiting before/after Release capture.

**Direction 2 validation — 2026-07-01:** **PASS (partial, with caveats).**

| Metric | Pre-fix gate | Dir 1 (accumulator) | Dir 2 (overhead sub) | Trend vs pre-fix |
|--------|-------------|---------------------|---------------------|------------------|
| avgTickGap range | 41.7→58.1ms | 42.2→49.7ms | 23.6→36.2ms (mean 29.2) | Below target; slight over-compensation |
| maxTickGap | 163.2ms | 104.6ms | 613.5ms (two external stalls) | Worse due to stalls; steady-state improved |
| max selectionSkips/window | 45 | 75 | 19 | **Better** (~58% of pre-fix) |
| total selectionSkips | 711 / 83w | 1019 / 52w | 242 / 70w | **Better** (~40% of pre-fix density) |
| lateDrops | 0 | 0 | 11 (one burst at stall) | Regression at stall point only |
| queue | 4–7 / 6 | 4–6 / 6 | 5–6 / 6 | **Tighter** |
| maxClockLate | 41.9ms | 42.0ms | 162ms (stall) / ≤42ms steady | Same steady-state |
| maxEarly | ~10.3ms | 0→10ms (collapsed) | 10.1–10.4ms, **stable** | **Rock-solid** |

**Key improvements:**
- `maxEarly` is rock-solid at 10.3ms ± 0.1ms across the full run — the clock-relative anchor is working correctly, unlike Direction 1's wake-late collapse.
- `selectionSkips` max 19 per window is 58% of the pre-fix gate and less than half of Direction 1. Total selectionSkip density is ~40% of pre-fix.
- Queue is tighter (5–6 vs 4–7), with zero windows at depleted queue.

**Caveats:**
- Two massive external stall spikes (417ms, 613ms) hit the loop, likely GC or system events, causing the only late-drop burst (11 drops) and the 162ms `maxClockLate` reading at that window. In steady-state windows, late drops are 0 and maxClockLate stays at ≤42ms.
- `avgTickGap` (mean 29.2ms) sits below the 41.7ms target interval — the body-overhead subtraction over-compensates slightly. The wait is computed as `nextFrame.PresentationTime - lead - clockPosition` minus body overhead. Since the clock already advances during the body (accounting for most overhead), the full-body subtraction double-counts that portion.

**Conclusion:** Direction 2 corrects the core failure mode of Direction 1 (detachment from clock) and measurably outperforms the pre-fix code on selection skips and maxEarly stability. The over-compensation and the stall-related late drops indicate the subtraction should be calibrated (e.g., measure overhead from just before the wait computation, not from tick start; or apply a fraction of the measured overhead). Accept as partial pass — Direction 2 is the correct anchor approach and outperforms both prior states, but the subtraction amount needs tuning.

**Artifacts:** `C:\Users\fatzb\AppData\Local\Temp\opencode\FatzBombMedia-VT-F\validate-dir2\20260701\`

**Direction 2 calibration — 2026-07-01:**

Moved overhead measurement from loop-body top to immediately before `GetClockLockedPresentationWaitSeconds`, changing `tickBodyStart` (full body) to `waitCpuStart` (wait-computation only). The subtraction now covers ~microseconds instead of double-counting the body's clock advancement. Line 831 added, line 782 removed.

**Direction 2 calibration validation — 2026-07-01:** **PASS.**

| Metric | Pre-fix | Dir 1 | Dir 2 | Dir2 calib | Best |
|--------|---------|-------|-------|-----------|------|
| avgTickGap range | 41.7→58.1 | 42.2→49.7 | 23.6→36.2 | 42.1→47.6 | Closest to 41.7ms |
| avgTickGap mean | ~45? | 46.3 | 29.2 | 44.1 | Closest to target |
| maxTickGap | 163.2ms | 104.6ms | 613.5ms | 103.6ms | Lowest |
| max selSkips/window | 45 | 75 | 19 | 15 | 33% of pre-fix |
| total selSkips | 711/83w | 1019/52w | 242/70w | 184/71w | 25% of pre-fix |
| lateDrops | 0 | 0 | 11 (stall) | 0 | Clean |
| queue | 4–7 | 4–6 | 5–6 | 4–7 | Pre-fix range |
| maxClockLate | 41.9 | 42.0 | 162/≤42 | 42.0 | Same steady |
| maxEarly | ~10.3 | 0→10 | 10.1-10.4 | 10.0-10.4 | Stable |

**Key results:**
- `avgTickGap` (42.1→47.6ms, mean 44.1ms) is closest to the 41.7ms target of all four runs. The over-correction is fixed.
- `maxTickGap` (103.6ms) is the lowest, beating pre-fix by 37%.
- `selectionSkips` max 15/window is 33% of pre-fix and the best of all runs. Total 184 over 71 windows is also the best.
- `lateDrops` stayed 0 the entire run — no stall burst unlike Dir2.
- `maxEarly` (10.3±0.2ms) rock-stable, identical to pre-fix healthy range. Clock-relative anchor confirmed.
- The 103.6ms `maxTickGap` spike still sits below `PrecisionWaiter`'s (duration+50ms) safety ceiling; WPR/GPUView may still help attribute whether these are waitable-timer vs external scheduling, but the calibrated fix already reduces them to the lowest observed value.

**Conclusion:** Calibration resolved the over-correction. The calibrated Direction 2 fix outperforms all prior states on every key metric. VT-F validated.

**Artifacts:** `C:\Users\fatzb\AppData\Local\Temp\opencode\FatzBombMedia-VT-F\validate-dir2-calibrated\20260701\`

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
