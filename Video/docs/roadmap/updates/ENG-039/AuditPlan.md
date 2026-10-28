# FatzBomb.MediaControl audit: implementation plan

2026-10-28. Based on the read-only audit of `main` (326a711), the owner's Windows smoke results
(RTX 5090, `scripts/smoke-audit.ps1`) and three GPU-reset logs. Written before any change;
progress is tracked in ENG-039 (`ENGINEERING_ISSUES.md`). Section 0's recommendations were
accepted (owner, 2026-10-28).

Each item gives:
- **what** is wrong;
- the **change** (files, members, code shape);
- the **tests** (unit tests that run in the Linux sandbox or on Windows CI, otherwise a manual smoke step);
- **size**: S (under an hour), M (half a day), L (more);
- **priority**: P0 must, P1 should, P2 nice.

Every PR is validated the same way before it opens:
1. build with 0 warnings;
2. run the Portable, Linux, Vulkan and Avalonia tests;
3. run `dotnet format`;
4. run the four boundary scripts;
5. run `/code-review medium --fix`.

Windows-only behaviour gets a short smoke script, in the same style as `smoke-audit.ps1`.

---

## 0. Decisions I need from you

| # | Decision | My recommendation |
|---|---|---|
| D-1 | **047A:** the CPU copy cost 0.3-0.8 % CPU at 4K30, with no drops. | **No-go.** Move 047A to `complete/` as "measured, not worth it". Optional: re-measure with a 4K60 clip first. |
| D-2 | **Public API removals** (alpha package, `0.1.0-alpha`): `MediaControlPlayer.Controls`, `.Video/.Audio/.Cdg/.Subtitle`, `MediaFile`, `IMediaRenderCoordinator.RunOnRenderThread`, `IVideoEngine.SetHostRenderEventDrivenPresentation`, `SharpArchiveAdapter.IsArchive`. | **Remove now.** Make them internal, or delete them. `[Obsolete]` first would cost a second PR and about 70 renamed call sites, to protect callers that don't exist. |
| D-3 | WPF `MediaSurface.ShowSecondaryOutput` family (WPF-only, untested, stale after a `Player` change). | **`[Obsolete]` now, remove later**. The READMEs document it, so give readers one release of warning. |
| D-4 | BASS-shaped public API: the `ICdgEngine.LoadPaired(..., audioHandle)` handle parameter, and `MidiLyricExtractor`. | **`[Obsolete]` now, remove with 037A.** |
| D-5 | Embedded subtitles load in the background (P5-1). This needs a new **`SubtitleTracksChanged` event** on the player (additive public API). | **Yes.** |
| D-6 | `Karaoke Source = null` currently keeps the old song playing. | **Release the player**, as `BackgroundSurface` does. |
| D-7 | Normalization: the audio engine boosts a quiet intro up to 8× for a moment; the video engine never does. Unify on the video rule? | **Yes** (audible: quiet intros no longer jump). |
| D-8 | Test dependency `Microsoft.Extensions.TimeProvider.Testing` for timer tests. | **No.** Use an injected interval and short real timers instead. |

---

## 1. What the smoke tests changed

**Confirmed:**
- refresh-rate struct packing (G2);
- GPU reset never recovers, in **every renderer** (G1);
- the UI freeze after a reset (G1b);
- black flash on every resize step (G4), with 423 export log lines in 15 s (G6);
- Avalonia close during rebuild, and the second screen reopening (B6);
- KAR lyrics allocating 234 MB/s (P5-2);
- the whole MKV read on open (P5-1).

**Not reproduced, so ranked lower:**
- the background-video loop glitch (B3; the trimmed path likely didn't run);
- the UI freeze on unloading a lightweight control (B7: 171 ms worst).

**Corrected findings:**
- **CD+G transparency (G7): not a bug.** All three renderers are already transparent before the first memory preset:
  - Software returns early;
  - the D3D11 shader returns 0 when `PaletteInfo.y < 0`;
  - Vulkan skips the layer.
  
  Your decision matches the current behaviour. What's left is a parity test and one skipped draw.
- **`SeekHelpers.NormalizeSeekTarget` is not dead.** `PlaybackCoordinator.Seek` uses it. Only `CdgPipeline`'s private copy is a duplicate.
- **Audio after the reset was fine.** Windows moved the default output from the LG to the MX279 and then the Yeti, and the player followed, as designed.

**What the GPU-reset logs show:**
- **3a, WPF Direct3D11, black picture.**
  1. The first failure after the reset was `CreateVideoProcessorInputView` → `E_INVALIDARG`, logged once as a plain D3D11 Warning.
  2. Every later frame failed silently.
  3. Core ignores `PresentNativeFrame`'s result (`_ = ...`), so the "presented=120" Perf lines are false.
  4. `DXGI_ERROR_DEVICE_REMOVED` was caught at `D3D11MediaCompositionPresenter.cs:452` and logged as a `Composition` Warning.
  5. Core starts recovery only on an Error whose category is exactly `D3D11.DeviceLost`, so it never started.
- **3c, WPF CD+G, frozen.**
  1. The video thread blocked inside a D3D11/DXGI call while holding the coordinator's `_lock`.
  2. The reset's display change made WPF resize, which waits on `_lock`, so the UI froze.
  3. The log is empty because the test player writes diagnostics to its log only after hopping to the (blocked) UI thread.
- **3d, Avalonia Vulkan, black picture.**
  1. The playback loop stalled for 14.5 s, most likely in an unbounded `vkWaitForFences` or a keyed-mutex submit.
  2. Avalonia then lost its own GPU context, so the player fell back to the frame sink.
  3. That fallback's `vkAllocateMemory` failed with `ErrorOutOfDeviceMemory`, which is how NVIDIA reports the lost device. It was logged once and never treated as a loss.
  4. The Direct3D11 device Vulkan uses to share images was also reset, and nothing checks it.

---

## 2. Cross-cutting design: one device-loss contract

Three reviewers proposed overlapping designs. This is the merged version, which all renderers use.

**Core contract** (public, one additive default member on `IMediaRendererBackend`, so existing renderers and every test double still compile):

```csharp
/// Whether the renderer's device (including a decoder device it adopted or shares) is lost.
/// Cheap and thread-safe: must not take the renderer's present lock or wait for the GPU.
/// Core calls it about once a second and after a failed present or surface call.
bool ProbeDeviceLost(out string? reason) { reason = null; return false; }
```

A renderer that finds a loss during a present just returns false from that present. Core then probes within the same frame, so no event is needed.

**Coordinator** (`MediaPresentationCoordinator`, new partial file `MediaPresentationCoordinator.DeviceHealth.cs`):
- **One entry point, `RequestDeviceLostRecovery(reason)`.** It:
  - logs one Error, "Renderer device lost (reason); recovering", under the player's diagnostic owner;
  - starts `_recoveryScheduler`;
  - catches `ObjectDisposedException` during teardown.
- **Triggers:** each one calls `ProbeDeviceLost`, then `RequestDeviceLostRecovery`.
  - Any failed `PresentComposition`, `PresentNativeFrame` (its result is ignored today), `ResizeSurface` or `AttachSurface`. Rate-limited to every 250 ms, and skipped while no surface is attached.
  - A **1 s health timer** (`ITimer` from an injected `TimeProvider`, with an overlap guard and a try/catch around everything). This covers pause, CD+G-only and audio-only playback.
- **While recovery runs**, hardware frames are dropped: they still point at the lost device.
- **Stall detector.** Hot paths stamp `_renderCallStart` and `_surfaceCallStart` around each renderer call. If a call has not returned within 3 s, the watchdog logs "Renderer call has not returned for N s (operation)" once. That confirms 3c's cause on the next smoke.
- **Recovery that outlasts a driver reset:**
  - backoff 0.5, 1, 2, 4, 8 s, 6 attempts;
  - after a final failure, retry every 10 s while surfaces are attached.
- **After recovery**, raise `DeviceRecovered`. The player then reopens the video at the same position and state (the existing mode-switch reopen path), because the decoder's device died too.
- **Last commit of the PR:** delete the category-string match (`OnDiagnosticOccurred`).

**Implementations:**
- **Direct3D11:** `ProbeDeviceLost` reads `ID3D11Device.DeviceRemovedReason`; any failure means lost.
  - Every catch that sees `DEVICE_REMOVED` returns false and logs the failing step, instead of a plain Composition warning.
  - `EnsureNativeFrameDevice` refuses to adopt a decoder device that is itself removed. Otherwise recovery would adopt the dead device again.
- **Vulkan:**
  - A **lost flag** replaces the backend's self-cleanup. This fixes the double device replacement and the stranded decoder (ENG-038).
  - `ProbeDeviceLost` runs a **heartbeat**: an empty `vkQueueSubmit` and a 250 ms fence wait, with three timeouts meaning lost. It also checks the Direct3D11 interop device's `GetDeviceRemovedReason`.
  - **Any** `VulkanException` probes the device. `ErrorOutOfDeviceMemory` counts as lost after three in a row, or at once within 5 s of a consumer context loss.
- **Software:** the default (never lost).

**Who recovers what (no races):**
- The coordinator alone recovers the renderer's device, and on D3D11 the decoder device it adopted. It then raises `DeviceRecovered`, and the player queues exactly **one** reopen on the control queue.
- `VideoPipeline`'s own decoder recovery covers only a decoder device the renderer does not own. When the renderer owns it, the pipeline's probe just stops hardware frames and leaves recovery to the coordinator.
- `VideoPipeline.Close` stops the decoder recovery scheduler before it frees the decoder, so the reopen cancels any recovery that is still running.
- Test: `DeviceRecovered_ReopensOnce_EvenIfRaisedTwice`.

**Decoder side** (`VideoPipeline`):
- While hardware decoding, check `IsDeviceLost(decoder device)` once a second, instead of only after three decode errors. FFmpeg keeps producing corrupt frames after a reset, so the error count never reached three.
- Frames flagged `AV_FRAME_FLAG_CORRUPT` count as failures.
- The coordinator also calls an internal `IGraphicsDeviceLossSink.OnGraphicsDeviceLost()` on the pipelines when recovery starts.

---

## 3. Batch 1: Core and player bugs (Linux-testable). One PR.

| ID | Item | Pri | Size |
|---|---|---|---|
| B1 | Software renderer + default config plays black video | P0 | S |
| B2 | Sync `Load` + PlayOnLoad starts playback outside the control queue | P1 | S |
| B4 | Diagnostics owner missing on sync control calls, PrepareNext, Analyze, fades, BASS metadata callback | P1 | S |
| B5 | FFmpeg audio fallback: not cancellable, fails late on >4 GB, allocates 160 MB | P1 | S |
| B6 | Avalonia player: close during rebuild, `async void` crashes, second screen reopening | P0 | S |
| B3 | Background-video reset races `Close` (use after free); work on BASS's mixing thread | P1 / P2 | M |
| B8 | `PlaybackPositionNotifier` start race | P1 | S |
| B9 | Audio leads video by up to one audio frame after every seek | P1 | S |
| B10 | Lost video packet on EAGAIN with a full queue | P2 | S |
| B11 | Network decoder swap re-seeks the shared audio; hardware re-probe reopens the URL every 5 s | P2 | S |
| B12 | Small items (below) | P2 | S each |

**B1, software renderer black video.**
- **Change:** `SoftwareRendererBackend` implements `IMediaRendererVideoDecodeDevice` with:
  - `VideoDecodeDeviceType => null`;
  - reason "the software renderer draws only CPU frames";
  - `CreateVideoDecodeDeviceContext` returns 0.
  
  Decoders then open in software, with one warning. There is no Core change, and docs note that Core now enforces this.
- **Test:** `RendererVideoDecodeServicesTests.TheSoftwareRenderer_OffersNoHardwareDevice_AndSaysWhyOnce`.

**B2, PlayOnLoad off the queue.**
- **Change:**
  - Delete `_ = Task.Run(HandlePlayOnLoadAsync)` (`MediaFile.cs:203`).
  - `LoadOnQueue` enqueues `File.StartPlayOnLoadAsync(loadGeneration, transportGeneration, token)` as a control operation from inside the load's turn.
  - It skips if `TransportGeneration` changed, so a `Pause`/`Stop` issued after `Load` wins.
- **Tests:**
  - `SyncLoad_PlayOnLoad_StopIssuedAfterLoadWins`;
  - `SyncLoad_PlayOnLoad_StartsOnce`.
  
  `TestAudioEngine` gains a settable `NormalizationReady`.

**B4, diagnostics owner.**
- **Change:** add `using var _ = DiagnosticRouter.EnterOwner(...)` in:
  - `ExecuteControlOperation` (and the `Dispose` turns);
  - `PrepareNextAsync`, `AnalyzeAsync`;
  - `RunFadeAsync`, `TransitionToPreparedNextAsync`;
  - `AudioEngine.OnNetworkMetadataSync`.
  
  Update ENG-025's "known limits".
- **Tests** (two players):
  - `PrepareNext_AnalysisFailure_ReachesOnlyItsPlayer`;
  - `SyncPlay_DiagnosticRaisedByEngine_ReachesOnlyItsPlayer`.

**B5, FFmpeg audio fallback.**
- **Change in `AudioStreamManager.OpenConverted`:**
  - pass `MediaOpenCancellation.Current`;
  - catch `OperationCanceledException` as a cancelled load, not an Error.
- **Change in `FfmpegAudioFallback`:**
  - add a pure `PlanOutput(duration, rate, channels, maxInMemory)` returning `Memory`, `File` or `TooLong`;
  - for `TooLong`, refuse **before** decoding, with a size warning;
  - for `File`, write straight to the spill file;
  - extract `CreateSpillFile`.
- **Tests:**
  - the three `PlanOutput_*` tests;
  - native `Take_WithCancelledToken_ThrowsAndLeavesNoScratchFile`.

**B6, Avalonia player** (quick fix now; R1 later replaces it with the shared controller).
- **Change** in `Player.Avalonia/MainWindow.axaml.cs`:
  - Store the rebuild `Task` (the WPF shape).
  - `OnClosing` awaits it before disposing.
  - Add a `DisposePanelAsync` with try/catch.
  - Set `_firstBuild = false` before init.
  - The init loop checks `_rebuildAgain`.
  - Wrap the `async void` handlers (`OnOpened`, `OnLayoutChanged`) in try/catch that writes to the status line and the log. Do the same for the WPF and WinForms handlers.
  - `PlayerPanel` takes `atStartup`, so `--second-screen` opens only on the first build.
- **Tests:** none possible (host UI).
- **Smoke:**
  - change Players or Renderer with `--second-screen`: no reopen;
  - change Players and press Alt+F4 at once: every created player logs a dispose line.

**B3, background-video reset** (`PipelineMediaEngine`).
- **Change:**
  - Only the background scheduler's worker touches `_backgroundVideoDecoder` after open.
  - `OnPlaybackEnded` and `Stop` call a new `RequestBackgroundRestart(resume)`, which queues the seek, decode and present onto that worker. Nothing heavy runs on the BASS mixing thread any more.
  - `ResetBackgroundVideoToStart` is deleted.
  - `PlaybackScheduler.StopAndWait` returns `bool`. If a run did not finish, `ClearBackgroundCore` does **not** dispose the decoder and logs a Warning: a leak is better than a crash.
- **Tests** (Linux native):
  - `OnPlaybackEnded` returns in under 20 ms, and the frame arrives on the background thread;
  - 50× `PlaybackEnded` raised concurrently with `Close()`: no crash;
  - `StopAndWait` returns false for a run that ignores cancellation.

**B8, position notifier race.**
- **Change:** capture the token in a local before `Task.Run` (`PlaybackPositionNotifier.cs:44`).
- **Test:** a 1000× Start/Stop storm raises no unobserved task exception.

**B9, audio leads video after a seek.**
- **Change:**
  - Add a pure `AudioSeekTrim.SamplesToSkip(framePos, target, rate, frameSamples)`.
  - `VideoAudioEngine.PushAudioFrame` trims the start of the frame that spans the seek target, instead of dropping it.
- **Tests:** `AudioSeekTrimTests` (spanning, at, before; 44.1 and 48 kHz).
- **Smoke:** an A/V sync clip stays aligned across 10 seeks.

**B10, lost packet on EAGAIN.**
- **Change:** `VideoDecoder` keeps a `_packetPending` flag and resends the packet on the next call when the queue was full. `Flush`, `Seek` and `Close` clear it.
- **Tests:** the existing native decode and seek tests.

**B11, network decoder swap.**
- **Change:**
  - After a network decoder swap, call `SeekPump.Request(position)` to realign the shared audio.
  - `ReprobeAndRestoreHardware` skips network sources.
- **Tests:** manual.

**B12, small items:**
- **Event snapshot context:** `MediaControlPlayer.Events.cs:325` becomes `await prerequisite.ConfigureAwait(ForceYielding | SuppressThrowing)`, so it never posts to the UI `SynchronizationContext`. Test: a counting context sees 0 posts.
- **`CdgPipeline` disposal:** make it `IDisposable` (it owns a `SeekPump` event handle); `MediaSession.Dispose` disposes it.
- **`RenderCurrentFrame` during a seek:** return early while a seek sequence is active and playing. This stops the unsynchronised clock access.
- **Test-player drag seek:** `SendDragSeekAsync` logs any exception ("Seek failed") instead of leaving it unobserved. Test with a throwing `TestMediaEngine`.
- **Test-player startup delay:** the 500 ms startup delay becomes cancellable on dispose.
- **Karaoke `Source = null`:** releases the player (D-6). Avalonia headless and WinForms tests.
- **Audio normalization (D-7):**
  - one `NormalizationGainPolicy` (the quick pass never boosts; final = max of quick and deep);
  - one shared bounded LRU `NormalizationGainCache` (1024 entries) replacing the two unbounded static dictionaries.
- **FFmpeg-only audio inside archives** (for example `.flac` + `.cdg` without `bassflac`):
  - `FileTree` classifies with `IsPlayableAudio`;
  - `OpenFromArchive` falls back to temp file → `OpenConverted`;
  - needs B5 first.
- **Device-change messages:** reword them to say whether this player follows the default output or stays on its device. Pass each player's device index to the polling monitors, so they watch that device and not the last one initialised.

---

## 4. Batch 2: Windows and GPU bugs. One PR; you smoke it.

| ID | Item | Pri | Size |
|---|---|---|---|
| G1 | Device loss detected and recovered on every renderer (section 2) | P0 | L |
| G1b | UI freeze after a reset; diagnostics lost when the UI thread blocks | P0 | M |
| G2 | `DWM_TIMING_INFO` packing (confirmed: 320 vs 292 bytes) | P1 | S |
| G4 | Black frame on every resize step (shared texture and frame sink) | P0 | S |
| G6 | Export log spam on resize (423 lines in 15 s) | P1 | S |
| G5 | Smaller GPU items | P2 | S each |
| G7 | CD+G parity test (no visual change) | P2 | S |
| W1 | WPF overlay window stays on screen when the surface is collapsed | P1 | S |
| W2 | WPF session events attached before load | P2 | S |
| W3 | WPF test player crashes on a read-only install | P1 | S |
| G3x | Decoder-side probe (section 2); ENG-037 at run time | P1 | M |
| G8 | Retained GPU background marked ready before any present succeeds (the "background layer could not be prepared" warnings from test 6) | P1 | S |

**G1, in commits:**
1. Core contract and coordinator (section 2). Tests in `MediaPresentationCoordinatorTests`, using `RecordingRendererBackend` with a settable probe result, `PresentResult` and `BlockPresent`:
   - `HealthProbe_LostWhileIdle_RecoversWithoutAPresent`;
   - `FailedPresent_ProbesImmediately`;
   - `HealthProbe_NoSurfaces_DoesNotEscalate`;
   - `HealthProbe_Healthy_NeverCleansUp`;
   - `DeviceLost_DuringDispose_DoesNotThrow`;
   - `BlockedRendererCall_RaisesOneStallDiagnostic`.
2. Direct3D11:
   - `ProbeDeviceLost`, and every `DEVICE_REMOVED` catch returns false, naming the failing step;
   - latch the `:452` and `:194` messages and name the failing step;
   - Core stops ignoring `PresentNativeFrame` (latched warning quoting `LastPresentFailureReason`).
   
   Windows CI on WARP: `D3D11DeviceHealthTests`.
3. Vulkan:
   - the lost flag;
   - the heartbeat;
   - bounded waits: 2 s fence timeout, then treat as lost; 50 ms keyed-mutex timeout, then treat as a consumer loss; heartbeat `TryEnter` on `QueueLock`;
   - a `VulkanStallMeter` warning when a call takes over 250 ms;
   - probe on any `VulkanException`; the out-of-memory rule;
   - the interop device check;
   - delete the redundant `vkQueueWaitIdle` in `DestroyExportedImages`.
   
   Lavapipe tests:
   - `DeviceLost_RefusesInitializeUntilCleanup`;
   - `DeviceLost_RefusesDecodeContext`;
   - `Heartbeat_HealthyDevice_NotLost`;
   - `ReportRenderFailure_OutOfMemory_ThreeTimes_EntersDeviceLost`;
   - `StarvedRing_PastStallTimeout_ReportsUnavailableOnceAndFails`.
4. Avalonia consumer:
   - add `MediaSharedGpuImageSet.ReportDeviceLost(reason)`;
   - on `PlatformGraphicsContextLostException`, report it, do **not** stick to the frame sink, and re-query Avalonia's GPU interop so the shared path comes back;
   - a 2 s watchdog on pending imports.
   
   Tests:
   - `ContextLost_ReportsDeviceLost_AndRequeriesInterop`;
   - `PlainUnavailable_StaysOnFrameSink`.
5. Decoder probe, and the reopen after `DeviceRecovered`. Tests:
   - `ShouldProbeDeviceLoss`;
   - `DeviceRecovered_ReopensVideoAtPositionAndState`;
   - `DeviceRecovered_AudioOnly_DoesNotReopen`.
6. Delete the category-string match.

- **Smoke:** repeat test 3 (3a to 3d). In each run the log should show "device lost … recovering", then "recovered", and the picture returns.

**G1b, UI freeze.**
- **Change (Core):** the presentation lock is split.
  - A new `_presentGate` serializes all presents. It keeps the ENG-023 order.
  - `_lock` is held only to build frame state, never across a renderer call.
  - Surface attach, resize, live-resize, detach and `BaseVisualTransform` take only `_lock`, so they never wait on a stuck present. Resize requests that find a present in progress are queued and applied at the next frame.
  - Host property setters (sync offsets, subtitle track, MIDI lyrics, transparent background, display mode, base transform) stop presenting on the caller's thread. They queue one coalesced `RefreshCurrentPosition` on the control queue.
- **Change (Player.Shared):** `PlayerViewModel` writes each diagnostic to the debug log on the raising thread before hopping to the UI thread, so a frozen UI no longer hides the log.
- **Tests:**
  - `SurfaceCalls_DoNotWaitForABlockedPresent`;
  - `Presents_StayInBuildOrder_AcrossPaths`;
  - `PropertySetters_RefreshOnTheControlQueueOnce`.
- **If the stall warning names DXGI `Present`:** use a frame-latency waitable swap chain with a 100 ms wait. This is a separate commit, only if the smoke points there.

**G2, refresh rate.**
- **Change:**
  - `DwmTimingInfo` gets `Pack = 1`.
  - Log the HRESULT once (category `DisplayTiming`, process-wide).
  - Add a follow-up note to ENG-018: the platform rate was always the 60 Hz fallback until now.
- **Tests:**
  - `DwmCompositionTimingTests.TimingInfo_MatchesPackedNativeSize` (asserts 292);
  - a plausible-interval test, Windows only.

**G4, resize black frame.**
- **Change:**
  - `D3D11SharedTextureBackend` and `D3D11ManagedFrameSinkBackend` get `Serves(consumer)`.
  - On a re-creation for the **same** consumer (a resize), skip `ClearActiveTargetToBlack` and the black present (`D3D11PresentationSurfaceController.cs:371-450`). The consumer keeps its last image until the next real frame.
- **Tests (WARP):**
  - `ManagedFrameSink_ResizeAnnouncesNoFrameUntilTheNextComposition`;
  - `SharedTextures_ResizeAnnouncesNoBlackFrame`.
- **Adapter:** `WpfCompositedSurfaceHost.Clear()` also drops the pending frame. Test: `Clear_DropsPendingFrame`.
- **Smoke:** repeat test 4: no flashes.

**G6, export log spam.**
- **Change:**
  - D3D11 and Vulkan log "exported" at Info only when the consumer or the selection changes; otherwise only to the debug output.
  - `Publish()` stops re-arming the "frame dropped" latch.
- **Tests:** `ResizeChurn_LogsOneExport` (WARP and lavapipe).

**G5, smaller GPU items:**
- **Decoder device without video support:** D3D11 refuses such a device **before** tearing down its surfaces, remembers the refusal, and `InitializeFromNativeDevice` returns true once adopted. Test: `EnsureNativeFrameDevice_RefusesWithoutCleaningUp`.
- **Shared-texture handle leak:** an NT handle leaked when the second shared texture failed. `SharedTexture` closes its handle if it was never published.
- **Present warnings:** swap-chain Present warnings are latched.
- **Vulkan `Upload`:** unmaps in `finally`. `PresentShared` aborts the ring write on any exception. `MediaCompositionFrame.IsBackgroundWellFormed` (Core) is shared by Software and Vulkan.
- **Vulkan barrier:** the decoded-image barrier uses the `COLOR` aspect.
- **Avalonia `CompleteImport`:** reports how many handles were actually consumed (`CompleteImport(int)`), so failed imports don't leak handles on Linux.

**G7, CD+G parity.**
- **Change:** skip the D3D11 CD+G draw when `BackgroundColorIndex < 0`. The shader already outputs transparent.
- **Tests:** one shared snapshot and expected pixel in three tests: Software, D3D11 on WARP, and Vulkan on lavapipe.

**W1, WPF overlay window:** close it when `!IsVisible`, and subscribe to `IsVisibleChanged`.

**W2, WPF session events:** `OnPlayerChanged` attaches the session events only when the surface is loaded.

**W3, WPF read-only install:** one native-path helper in the linked `PlayerNativeLibraries.cs`, with the IOException guard. WinForms uses it too.

**G8, retained GPU background marked ready too early** (`MediaPresentationCoordinator`).
- **What:** `SetRetainedGpuBackgroundNoLock` and `StoreRetainedHardwareBackgroundFrame` mark the GPU background as retained before any present succeeds. When that present failed (no target attached yet), later refreshes ask the renderer for a background it never received.
- **Change:**
  - Keep a pending revision and a presented revision.
  - Mark the background retained only after `PresentMediaComposition` returns Presented, for that revision.
  - Until then, refreshes go out without a background instead of failing.
  - Don't warn about "no target" while no surface is attached (Core's own surface count, the ENG-012 pattern).
- **Tests:**
  - `HardwareBackground_FailedPresent_IsNotRetained`;
  - `HardwareBackground_PresentedFrame_IsRetainedForRefresh`;
  - `StoredHardwareBackground_WithoutPresent_IsNotRetained`.
- **Smoke:** repeat test 6: the two warnings are gone.

**G3x, ENG-037 at run time** (needs R2 and R3 from batch 4 first):
- `NativeVideoFrameResource.DecodeFallback` (additive): when the renderer refuses a hardware frame, it asks the decoder to switch to software.
- The pipeline swaps on its recovery scheduler, never inline.
- It disables the 5 s hardware re-probe for that media, which also fixes the black paused first frame.

---

## 5. Batch 3: dead code. One PR, after batches 1 and 2.

**Safe deletions (private or internal):**
- `VideoDecoder.GetElapsedMilliseconds`, the never-read transfer and buffer timers, and the per-frame Stopwatch;
- Vulkan `LastVideoFailure`, `VulkanExportedImageSet.HasSemaphores`, and the test-only `GetExportCapabilities(bool, bool)`;
- `IAudioFilter` and `BassEqualizerFilter`'s `Id`, `LatencySeconds`, `IsActive` and `Remove()`;
- D3D11 `IsReady` ×3, `TryGetActiveTargetSnapshot` and `TryGetDirectCompositionOverlayTargetSnapshot`;
- `D3D11DeviceLostClassifier.IsDeviceLost(IntPtr)` and its test;
- `MediaFile`'s 12 public `Load`/`LoadAsync` wrappers. One test call moves to the internal overload.

**Public, per D-2:**
- `MediaControlPlayer.Controls` and the `MediaControls` class become internal. `.Video/.Audio/.Cdg/.Subtitle` are deleted (no callers anywhere). Test code compiles unchanged through `InternalsVisibleTo`.
- `MediaFile` becomes internal.
- `IMediaRenderCoordinator.RunOnRenderThread` is deleted (5 pass-through implementations).
- The host-render-event mode is deleted:
  - `IVideoEngine.SetHostRenderEventDrivenPresentation`;
  - the field and its 4 branches in `VideoPipeline`;
  - 4 coordinator calls;
  - the test stub.
- `SharpArchiveAdapter.IsArchive` is deleted.

**Per D-3 and D-4:**
- `[Obsolete]` on the WPF `ShowSecondaryOutput` family and on `ICdgEngine.LoadPaired`'s handle.
- `MidiLyricExtractor` becomes an `[Obsolete]` forwarder to an internal `MidiLyricCueBuilder`.
- Internal code must not call obsolete members, because warnings are errors.

**Kept:**
- `AudioDeviceChangeKind.Unrecoverable` (useful for future device recovery);
- the `VideoDecodeCapabilities` helpers;
- the reserved surface type ids;
- test-only seams.

**Docs:** update `CURRENT_ARCHITECTURE.md` (`IAudioFilter`, `MediaFile` mentions) and the two READMEs.

**Tests:** build, plus the existing suites.

---

## 6. Batch 4: refactors. One PR per group, in this order.

**R1, `PlayerSetController<TSlot>` in Player.Shared** (M-L). It replaces the three copies of:
- the rebuild loop;
- full screen and F11/Escape;
- the stats tick;
- the closing sequence.

Shape:
- `IPlayerSlot` is the view: initialize, dispose, toggle event, status, full screen, flyout.
- `IPlayerSetHost<TSlot>` is the window: requested count, create, attach, detach, layout, window full screen.
- The controller owns:
  - `RebuildAsync`, which is coalescing, exception-safe and returns the in-flight task;
  - `ShutdownAsync`, which awaits the rebuild, disposes everything and returns whether the host may close;
  - full screen, Escape (one rule for all hosts) and the 1 s stats timer.
- `PlayerStartupOptions.ForBuild` and `OpensSecondScreenAtStartup` replace three hand-copied conditions.
- The B6 quick fix is deleted.

**Tests** (`PlayerSetControllerTests`, fake slots and host):
- coalescing;
- order of dispose and create;
- the first-build flag even when the first build is superseded;
- shutdown during a rebuild (the B6 regression);
- dispose failures;
- full-screen order;
- the F11/Escape rules;
- the stats tick stops after shutdown.

Each host changes in its own commit, followed by the existing 046A smoke.

**R2, decode-device seam** (S, a prerequisite for ENG-037):
- Add an internal `IRendererVideoDecodeAttachment`, implemented by `PipelineMediaEngine`. The coordinator uses `as IRendererVideoDecodeAttachment`.
- Decoders and pipelines take `RendererVideoDecodeServices` directly, with no downcasts. `TestMediaEngine` implements the interface.
- Test: `AttachesAndDetachesItsDecodeDevice_ThroughTheEngine`.

**R3, `VideoPipeline.TryReplaceDecoder`** (S): one decoder-replace helper used by the swap and the software fallback.

**R4, `MediaControlPlayer.FreeSessionGraph`** (S): one teardown for Reset and Dispose. Do it with G1's reopen subscription.

**R5, adapter copies:**
- `LatestFrameMailbox` in Core, replacing three frame sinks, with the 2× spare-buffer cap and checked arithmetic (M);
- `KaraokeLayout.Fit` and `PresentationPlacement.Center` (S);
- `LightweightSurfaceCore` and `MediaSurfaceBookkeeping` in Core (M each);
- WPF's two visibility writers merged: last, only if needed.

**R6, Player helpers:**
- `PlayerRendererRules.DescribeDecoding` gives one table for the decoding status text (the "hardware decoding (Vulkan Video)" string), with a `[Theory]` pinning the wording;
- a Windows `WindowsPlayerEngineFactory` base shared by WPF and WinForms (M, optional).

**R7, renderer copies:**
- `MediaCompositionFrame.ResolveClearArgb` and `ColorMath.PremultiplyBgra`;
- `MediaPresentationFrameSinkHandle.TryResolve`;
- a Core `NullSubtitleRasterizer`;
- a `CompositionPresentOutcome` struct;
- inside Vulkan: one memory-allocation helper, one `VulkanResult.Check`, and a shared `CreateFramebuffer`;
- D3D11: fold the four pass-through hardware-frame layers into one `D3D11HardwareFramePresenter`, and delete the always-identity DirectComposition transform (M).

**R8, audio copies:**
- `FfmpegAudioDecoderSetup` (the decoder and swr setup ×2);
- `PollingLoop` (the device-monitor loop ×2);
- `OpaqueTokenRegistry<T>` (frame sink and shared image);
- `MediaDemuxer.DiscardAllStreamsExcept` used by the three discard loops;
- `CdgPipeline` uses `SeekHelpers.NormalizeSeekTarget`.

---

## 7. Batch 5: performance. One PR; measure before and after.

| ID | Item | Evidence | Size |
|---|---|---|---|
| P5-2 | KAR raster: cache DWrite formats, per-cue layouts, the WIC bitmap and render target per size, hit-testing instead of prefix layouts; the rasterizer writes into a Core-pooled buffer (new default overload on `ISubtitleRasterizer`). Core skips the re-raster when the cues and progress are unchanged. | 234 MB/s measured | M |
| P5-1 | Embedded subtitles decode on a background task: cancellable, generation-checked, raising `SubtitleTracksChanged` (D-5). Sidecar vs embedded auto-selection kept; a user's choice is never overridden. | 1.95 GB read on open | M |
| P5-3 | D3D11 shader bytecode cached statically (9 compiles per media open today) | code | S |
| P5-4 | D3D11 video-processor input views cached per pool slice | code | S |
| P5-5 | Local files: the video cursor discards audio and subtitle streams; dead local fan-out in `SharedDemuxer` removed | code | M |
| P5-6 | Seek loops: `Queue` plus a lock instead of enumerating a `ConcurrentQueue`; display-locked mode allocation-free | code | M |
| P5-7 | `HostEventDispatcher` uses `Delegate.EnumerateInvocationList`; WinForms paint brush; single `Marshal.Copy`; GDI stock brush | code | S |
| P5-8 | Bootstrap without BASS: no rescan per player | code | S |
| P5-9 | Archive audio held once, not twice | code | S |
| P5-10 | Vulkan per-frame image views and holders; persistent readback map (optional) | code | S |
| P5-11 | Composition planning: drop the per-frame `CompositionLayerSchedule`, struct layers (measure first) | ENG-026 | M |

**Tests:**
- P5-2: `RenderKaraoke_SteadyWipe_AllocatesUnder64KbPerFrame` and `RenderKaraoke_CachedPath_MatchesUncachedPixels` (Windows), and `UnchangedCues_DoNotRasterizeOrAllocateText` (Core).
- P5-1: `EmbeddedTracksArrivingLater_*`, `NextOpen_CancelsScan_AndLateResultIsDropped`, `SubtitleTracksChanged_IsForwardedToHost`, and native `DecodeTracks_StopsWhenCancelled`.
- **Smoke:** repeat tests 9 and 10. Expect under 10 MB/s for the KAR, and the MKV opening without reading the whole file.

---

## 8. Dropped (not worth their code)

- The UI freeze when a lightweight control releases its player (B7). Not reproduced; 171 ms worst. Possible later as an async release.
- The latency query on every `Position` read: unmeasured, and ENG-021/037A replaces that code.
- Disposing `PlaybackScheduler`'s wait event: the finalizer handles it, and disposing risks `ObjectDisposedException`.
- `HardwareDecoderScanner`'s D3D11/D3D12 twin loops.
- The test players' lightweight-surface windows (toolkit UI).
- The unbound `PlayerViewModel` members. Optional: show `LoadedMediaText` in WPF.
- No release to `QUEUE_FAMILY_EXTERNAL` on Vulkan FD/NT exports. Reopen under ENG-038 if AMD or Intel show corruption.
- The `IMediaControlSession` default members (they keep binary compatibility).
- Moving `Core/Graphics/Planning` into the D3D11 package.

---

## 9. Order and effort

| PR | Contents | Effort | Windows smoke |
|---|---|---|---|
| 1 | Batch 1 bugs | ~1.5 days | B6, B9 |
| 2 | Batch 2 GPU bugs (G1 is the bulk) | ~3-4 days | test 3 (reset), test 4 (resize), test 2 |
| 3 | Batch 3 dead code + API decisions | ~0.5 day | one playback per path |
| 4a | R1 player controller | ~1 day | 046A smoke |
| 4b | R2-R4 Core seams, then G3x (ENG-037) | ~1 day | paused first frame |
| 4c | R5-R8 copies | ~1.5 days | playback per adapter |
| 5 | Batch 5 performance | ~2 days | tests 9 and 10 |

Each PR also adds or updates an entry in `docs/roadmap/ENGINEERING_ISSUES.md`. The GPU-reset work also gets a stage update with smoke steps, per AGENTS.md.
