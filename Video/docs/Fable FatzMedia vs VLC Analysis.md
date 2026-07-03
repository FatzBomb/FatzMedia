# FatzBombMedia.MediaControl vs VLC — Engine Analysis

**Date:** 2026-07-03
**Scope:** `FatzBombMedia.MediaControl` (Core engine) compared against VLC's playback engine (libvlccore: input/demux, es_out, decoder threads, `vlc_clock`, vout/aout). Interface and plugin ecosystems intentionally out of scope. Focus: memory, pipeline, performance, timing, threading, bottlenecks, and production readiness.

**Method:** All FatzBombMedia claims below were verified directly against source with file:line citations (current working tree). VLC comparisons are based on documented libvlc/libvlccore architecture (single demuxer → es_out fan-out → per-ES decoder threads → clock-slaved vout/aout), flagged where they are general knowledge rather than side-by-side measurement. No tests or benchmarks exist in this repo, so all performance claims are architectural reasoning, not measured numbers.

---

## Executive Verdict

FatzBombMedia.MediaControl is an unusually disciplined engine for a `0.1.0-alpha`: every hot-path queue is bounded, teardown is generation-guarded, frame buffers are pooled, hardware decode is genuinely zero-copy, and the CDG karaoke subsystem is better-architected than anything VLC ships. As a **karaoke-focused Windows engine**, it is already competitive in its niche.

Against VLC as a **general production media engine**, it is materially behind in four areas that matter more than any feature gap: **(1) no error reporting channel of any kind, (2) no GPU device-lost recovery, (3) a timing model that slaves video to a polled audio position with timer-based (not display-locked) pacing, and (4) zero automated verification.** These are the difference between "works great on my machine" and "survives 10,000 machines," which is exactly the gap VLC spent 20 years closing.

Scorecard (vs VLC engine):

| Area | Verdict | One-line reason |
|---|---|---|
| Pipeline structure | **Behind** | Demux+decode combined; file demuxed twice (video + audio paths) |
| Memory discipline | **Ahead/Equal** | Everything bounded and pooled; VLC's picture_pool is equivalent but FBM has fewer copies on the HW path |
| HW decode path | **Equal** | D3D11VA zero-copy clone → VideoProcessorBlt is the same class of path as VLC's d3d11 vout |
| SW decode path | **Behind** | One extra full-frame CPU copy + `UpdateSubresource` instead of mapped dynamic textures |
| Master clock / A/V sync | **Behind** | Polled BASS position + drift estimator vs VLC's unified clock with aout latency feedback |
| Frame pacing | **Behind** | Free-running high-res timer vs display-locked scheduling; no waitable swapchain |
| Seek UX | **Ahead** | Latest-wins preview/settle pump with adaptive budgets beats stock libvlc scrubbing |
| Threading model | **Equal-** | Clean scheduler abstraction, `System.Threading.Lock` throughout; but vsync Present inside two locks is a real contention flaw |
| Robustness / resilience | **Far behind** | No error events, no device-lost/TDR recovery, no audio device hot-plug, silent stall on mid-stream I/O error |
| Karaoke (CDG/pitch/tempo) | **Far ahead** | Indexed-color GPU CDG pipeline, key change, loudness normalization — VLC has nothing comparable |
| Production operability | **Far behind** | No tests, no CI, Debug-only logging, no diagnostics channel |

---

## 1. Pipeline Architecture

### 1.1 What FatzBombMedia does

- `PlaybackCoordinator` wires three sibling pipelines (Audio, Cdg, Video) around one `MediaSession` holding shared `MediaState` + `PlaybackClock` (`Pipeline/PlaybackCoordinator.cs:16-29`, `Pipeline/MediaSession.cs:5-16`). A `ClockAudioEngine` (wall-clock virtual audio, `Pipeline/ClockAudioEngine.cs:6-26`) substitutes when no real audio exists.
- **Demux and decode are combined in one stage.** `VideoDecoder.DecodeNextFrame` does `av_read_frame` → send/receive → convert → queue in a single synchronous call, discarding non-video packets (`Core/Video/VideoDecoder.cs:120-147`).
- **The container is demuxed twice for a video file.** `VideoAudioEngine` opens the same file with a second `AVFormatContext` and demuxes independently for the audio track (`Core/Audio/VideoAudioEngine.cs:187-216`, skipping non-audio packets at `:280-291`). Seeks are two independent `av_seek_frame` calls (`VideoDecoder.cs:957-972`; `VideoAudioEngine.cs:794-814`).
- Decoded audio PCM (FFmpeg → swr float) is *pushed* into a BASS push stream wrapped in a BassFx tempo stream (`VideoAudioEngine.cs:626-667, 771`).
- Backpressure is watermark-based everywhere: video decode halts at target queue depth (`Pipeline/VideoPipeline.cs:1115-1121`), audio push throttles at 4 s buffered / resumes at 2 s (`VideoAudioEngine.cs:14-15, 697-724`).

### 1.2 How VLC does it

One input thread demuxes once; `es_out` fans packets out to per-ES decoder threads through bounded FIFOs; outputs (vout/aout) consume from decoder-owned queues. Demux, decode, and display are fully decoupled stages with independent threads and explicit inter-stage buffering.

### 1.3 Assessment

**Bad — double demux.** This is the biggest structural divergence and it costs real resources: 2× container parsing, 2× file I/O (painful on network shares and during scrub bursts, where both contexts seek independently), and 2× seek work. Correctness holds because both sides discard-to-target, but VLC pays half the I/O for the same result. For local karaoke files this is tolerable; for a general engine it is a design debt that also blocks future features (subtitle streams, multi-track audio, chapters all want a single shared demuxer).

**Bad — combined demux+decode.** Because `av_read_frame` and `avcodec_receive_frame` run in the same loop pass (max 3 packets per pass, `VideoPipeline.cs:48, 1113`), a slow read (disk hiccup, archive extraction) directly stalls decode, and a slow decode stalls reads. VLC's stage separation isolates these. FFmpeg's internal frame threading (`thread_count=0`, `VideoDecoder.cs:337-338`) partially compensates for decode throughput but not for I/O stalls.

**Good — backpressure design.** Watermark hysteresis on audio and byte+time-bounded video queues are exactly right, and simpler than VLC's equivalent machinery. No unbounded collection exists anywhere in the hot path (verified by audit).

**Good — pipeline fallback logic.** Hardware decode failure at startup rebuilds a software decoder automatically (`VideoPipeline.cs:1147-1167`); codec-open failure falls back HW→SW inside the decoder (`VideoDecoder.cs:341-353`). This mirrors VLC's decoder fallback behavior.

---

## 2. Memory

### 2.1 Where FatzBombMedia is genuinely good

- **Bounded everything.** Decoder queue hard cap 24 (`VideoDecoder.cs:66`); effective target = min(96 MiB budget, 250 ms of frames), clamped 4–12 (`VideoPipeline.cs:45-47, 1724-1734`). Audio 4 s/2 s watermarks. CDG dirty rects collapse to full-frame past 64 (`Core/Cdg/CdgStateMachine.cs:456-465`). Seek requests are a single latest-wins slot. **Nothing unbounded was found.** This is equal to VLC's picture_pool discipline, achieved with less machinery.
- **CPU frames use `ArrayPool<byte>.Shared`** with consistent rent/return across present, release, and seek paths (`VideoDecoder.cs:615, 933-955`; `VideoPipeline.cs:1869-1890, 1950-1954`). No steady-state FFmpeg leaks were found — packets, frames, swr buffers, hw device contexts all balanced (`VideoDecoder.cs:834-914`; `VideoAudioEngine.cs:469-518`).
- **Hardware frames are zero-copy.** D3D11VA output frames are `av_frame_clone`d (refcount bump on FFmpeg's texture-array pool — no copy) and the raw texture+slice is queued (`VideoDecoder.cs:663-697`). YUV→RGB happens on the GPU via `VideoProcessorBlt` (`Core/Graphics/D3D/Video/D3D11VideoProcessorPresenter.cs:106-128`). This is structurally the same as VLC's d3d11 vout and is the correct design.
- **CDG memory is elegant:** two 64.8 KB index buffers + 16-entry palette, indexed→RGB expansion done *in the pixel shader* with dirty-rect sub-uploads (`CdgStateMachine.cs:12-14`; `Core/Graphics/Composition/D3D11/D3D11CompositionLayerState.cs:416-493`; shader `D3D11MediaCompositionPresenter.cs:1146-1171`). VLC decodes CDG to full RGB pictures on the CPU. **FatzBombMedia is better here.**

### 2.2 Where memory is worse than VLC

- **ArrayPool bucket overshoot silently doubles footprint.** An 8.3 MB 1080p BGRA frame rents a 16 MiB power-of-two bucket, but the budget math uses exact frame size (`VideoPipeline.cs:1724-1734`). Real steady-state software 1080p footprint is ~160–190 MB CPU (queue 8×16 MiB + retained frame + snapshot + copy buffer) vs the nominal ~96 MB budget. VLC's picture pool allocates exact-size pictures.
- **The software path carries one extra full-frame CPU copy vs VLC:** sws_scale → pooled buffer → *copy into retained coordinator buffer* (`Core/Graphics/NativeMediaPresentationCoordinator.cs:1001-1010`) → `UpdateSubresource` → GPU. VLC maps a dynamic/staging texture and converts/copies once. At 4K60 software this difference alone is ~2 GB/s of avoidable memory traffic.
- **Background/sidecar video allocates a fresh `new byte[]` full-frame copy per frame** (`Pipeline/PipelineMediaEngine.cs:468-470`) — at 4K that is ~33 MB of LOH garbage per frame. This is the single worst allocation in the codebase.
- **CDG snapshots allocate ~65 KB per snapshot** up to ~75 Hz ≈ 4.9 MB/s Gen0 worst case (`CdgStateMachine.cs:145-169`). Tolerable, but a double-buffer would zero it.
- **Hardware frame release has no GPU fence.** `av_frame_free` recycles the FFmpeg pool surface right after `Flush()` submits the blit, without confirming completion (`VideoPipeline.cs:1888`; `VideoDecoder.cs:1004-1014`). VLC holds picture references until display release. Risk is a driver-dependent visual artifact (same device, so no crash) — low severity, but a correctness gap.

---

## 3. Timing, Clock, and A/V Sync

This is the deepest engineering gap versus VLC and the area where "it looks fine" and "it is right" diverge the most.

### 3.1 The FatzBombMedia model

- **Three clock sources selected by content:** smoothed BASS audio position for video-with-audio (`Pipeline/AudioPresentationClock.cs`), a `Stopwatch` wall clock for video-without-audio/standalone CDG (`Pipeline/PlaybackClock.cs:21`), and raw BASS position for pure audio/CDG.
- Video **slaves to audio by polling** a lock-free position snapshot at presentation time (`VideoPipeline.cs:1177-1188`; snapshot at `VideoAudioEngine.cs:98-122`). The `AudioPresentationClock` empirically estimates rate over ≥80 ms windows (EMA 0.08), soft-corrects drift ≥12 ms by 12%, hard re-anchors at ≥90 ms, and clamps monotonic (`AudioPresentationClock.cs:11-15, 57-69, 99-109`).
- Frame selection: dequeue frames with PTS ≤ target + adaptive lead (0.5–1.25 refresh intervals, auto-tuned per 120-frame window); newer candidates displace older (implicit catch-up drop); frames >180 ms late are dropped (`VideoPipeline.cs:50-58, 76, 1651-1701, 1796-1821`).
- Pacing uses a high-resolution waitable timer (`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`, `Core/Threading/PrecisionWaiter.cs:36-91`) in either clock-locked or fixed-interval mode (`VideoPipeline.cs:944-989`).

### 3.2 The VLC model

One unified `vlc_clock` per input; the audio output is master and **feeds measured output latency back into the clock**; the vout thread schedules each picture against a *predicted display time* with render-ahead, explicit frame repeat, and drift correction via audio resampling when needed.

### 3.3 Assessment

**Good — the smoothing/drift design is legitimate.** The rate-estimating clock is a clever property: video automatically follows tempo/pitch changes without explicit rate plumbing (`AudioPresentationClock.cs:74-89`), which is exactly what a karaoke engine needs. The monotonic clamp and the lock-free snapshot (avoiding contention with the audio decode lock) are correct engineering.

**Bad — no audio output latency compensation.** BASS device latency is never queried (verified: no latency read anywhere in `VideoAudioEngine.cs`/`AudioEngine.cs`). The adaptive lead can absorb a *constant* offset only by observing lateness statistics, indirectly and slowly. A user on a Bluetooth output (100–300 ms device latency) gets permanent lip-sync error the system cannot see. VLC's aout reports latency into the clock continuously. **This is the single biggest sync correctness gap.**

**Bad — timer-based pacing beats against the display.** There is no waitable swapchain object and no vblank alignment (verified: no `GetFrameLatencyWaitableObject`; swapchains are FlipSequential without waitable flag, `Core/Graphics/D3D/SwapChain/D3D11SwapChainFactory.cs:13-58`). A free-running timer at source cadence inevitably drifts across the refresh phase, producing periodic ±1-frame judder that the adaptive lead dampens but cannot eliminate. Worse, the composition path presents with `Present(0)` (`D3D11RendererPresentationServices.cs:286-291`) relying purely on the software timer, while the HW fast path uses `Present(1)`. VLC's vout schedules against predicted display time. For 24 fps film content on a 60 Hz display — the classic judder torture test — VLC will be visibly smoother.

**Bad — no explicit frame repeat.** "Repeat" is only the retained swapchain content (`VideoPipeline.cs:1704-1708`); there is no 3:2-cadence-aware scheduling. Combined with timer pacing, cadence stability at non-integer ratios is worse than VLC's.

**Equal — end detection and post-seek handling.** Tolerance-based end detection (`VideoPipeline.cs:1339-1392`) can fire ~120 ms early, but VLC has its own EOF heuristics for broken metadata; both are pragmatic.

---

## 4. Seek

**Ahead of VLC.** This is the most engineered subsystem in the codebase and it shows:

- Non-blocking latest-wins pump on a dedicated thread (`VideoPipeline.cs:427-438, 525-542`), with a **two-phase preview/settle** design: scrub bursts get fast approximate frames (±2.0 s acceptance), then a quiet pump commits an exact discard-to-target seek that never shows an earlier frame (`SeekWorkerLoop` `:572-634`; `IsSeekFrameAcceptable` `:1596-1605`).
- Adaptive pump interval from an EMA of measured seek cost (75–300 ms, `:710-738`); decode budgets differ for HW vs SW decoders (`:80-87`).
- Generation counters retire in-flight loops (`:30, 463`); stale queued frames are dropped against the committed target (`:1627-1647`); post-seek recovery windows suppress late-drops to hide the classic post-seek hitch (`:1254-1315`).
- Audio side is sample-accurate via `_discardAudioBeforeSeconds` + prebuffer before resume (`VideoAudioEngine.cs:392-452`).
- CDG seek is deterministic replay from packet 0 — correct-by-construction state at any position (`Core/Cdg/CdgEngine.cs:165-212`).

Honest caveats: commit seeks are "close-enough" not frame-exact when the decode budget expires (documented at `VideoPipeline.cs:1444-1447`); `SeekCore` holds `_transportGate` for up to ~0.5 s including warm-up decode, blocking Play/Pause/Stop behind it (`:447-521, 1286-1306`); and a narrow race lets a seek accepted between `_isOpen = true` and `StartSeekWorker()` be silently discarded (`:191, 205, 528-533`).

Stock libvlc scrubbing (seek → flush → wait for decoder to reach target) has no preview phase and no adaptive budgeting. For scrub-heavy karaoke hosting UX, FatzBombMedia's design is better.

---

## 5. Threading

### 5.1 Census

Video-with-audio playback runs ≥5 managed threads — decode (`VideoPipeline.cs:833-838`), presentation at AboveNormal (`:852-857`; priority bump in `Pipeline/PlaybackScheduler.cs:160-163`), seek worker, operation queue (`Api/MediaControlPlayer.cs:39`), plus the CDG coordinator loop when attached — plus BASS native threads. Comparable in count to VLC (input + decoders + vout + aout); the `PlaybackScheduler` abstraction with named threads, cancellation, and observed task exceptions (`PlaybackScheduler.cs:83, 139`) is clean.

### 5.2 Real problems

1. **Vsync `Present` inside two locks.** `PresentFrame` raises `FrameReady` synchronously while holding `_presentationLock`; the coordinator handler then does `VideoProcessorBlt` + DXGI `Present` while *also* holding the coordinator `_lock` (`VideoPipeline.cs:1869-1890`; `NativeMediaPresentationCoordinator.cs:538-544`; `D3D11SwapChainPresenter.cs:34-41`). With `Present(1)` this can hold both locks up to a full refresh interval per frame, stalling pause/stop/seek and the CDG render loop. **This is the most important contention flaw in the engine.** VLC never presents while holding transport-visible locks.
2. **Sync transports block up to 2 s.** `StopAndWait` → `Task.Wait(2000)` (`PlaybackScheduler.cs:74-81`), called from Pause/Stop/Close/SeekCore. Documented in AGENTS.md, and async wrappers exist via the operation queue — but the sync API is still a footgun VLC's API avoids (libvlc stop is async by design in 4.x).
3. **Long work under `_decodeLock` / `_transportGate`.** Open/Stop run up to 240 decode attempts under `_decodeLock` (`VideoPipeline.cs:196-202, 401-408, 1130-1145`); seek holds `_transportGate` through warm-up decode (§4).
4. **Events fire from background/native threads.** `PlaybackEnded` can fire from the BASS callback thread with no try/catch around host handlers (`Core/Audio/AudioEngine.cs:475-487`) — a throwing handler propagates into a native callback. Core marshals nothing by design (host's job), which is defensible, but the BASS-callback case needs hardening.

Overall: the lock discipline (`System.Threading.Lock`, lock-free position snapshot, generation guards) is above-average .NET engineering; the two flaws above are specific and fixable rather than systemic.

---

## 6. Performance and Bottlenecks (4K60 / high-refresh honesty check)

| # | Bottleneck | Evidence | Severity |
|---|---|---|---|
| 1 | SW path memory traffic: sws_scale + 2 CPU copies + `UpdateSubresource` ≈ 4 GB/s at 4K60 | `VideoDecoder.cs:627-629`; `NativeMediaPresentationCoordinator.cs:1001-1010`; `D3D11CompositionLayerState.cs:175` | **4K60 SW will not sustain.** HW path is the only viable 4K60 route (and it is sound). |
| 2 | 4K buffer depth collapses to 4 frames (~66 ms) — 96 MiB ÷ 33 MB clamps to min | `VideoPipeline.cs:47, 1724-1733` | One decode hiccup >66 ms starves presentation at 4K. |
| 3 | Up to ~13 retained refs against FFmpeg's D3D11 surface pool (12 queued + current) with no `extra_hw_frames` config | `VideoDecoder.cs:674-693` (absence of pool sizing verified) | Potential `avcodec_receive_frame` stall for some codec/GOP configs. Unverified risk. |
| 4 | Presentation tick clamped to ≤120 Hz (`Math.Clamp(1/fps, 1/120, 1/15)`) | `Core/Video/VideoPresentationTiming.cs:16` | >120 fps content cannot be paced at source cadence; 240 Hz displays underserved. |
| 5 | DWM refresh query failure hard-falls to 60 Hz | `VideoPipeline.cs:1011-1037` | Wrong pacing on exotic display stacks unless host overrides. |
| 6 | Per-frame COM churn: new RTV per pass, new VideoProcessorInputView per blit, `Flush()` after every pass | `D3D11MediaCompositionPresenter.cs:315, 599, 645`; `D3D11VideoProcessorPresenter.cs:106-118, 135` | Small but measurable; VLC caches views and lets the driver batch. |
| 7 | Per-frame managed allocs: `VideoFrameData`, `VideoFrameEventArgs`, background-video `new byte[]` | `VideoDecoder.cs:632-640`; `VideoPipeline.cs:1934-1948`; `PipelineMediaEngine.cs:468-470` | Item 3 of these (background video) is the only serious one. |
| 8 | Per-Open GPU teardown in HW mode: each `VideoDecoder.Open` creates a fresh FFmpeg D3D11 device → full compositor/swapchain rebuild per file | `VideoDecoder.cs:425`; `NativeMediaPresentationCoordinator.cs:336-349`; `D3D11DeviceResources.cs:81-108` | Track-to-track transition cost; VLC reuses its decoder device across items. Directly hurts a karaoke queue workflow. |

**Where performance is equal or better than VLC:** the HW decode→present path (zero-copy, shared device, `ID3D11Multithread` protection set at `D3D11DeviceResources.cs:121-122`); the DirectComposition base/overlay split that lets 75 Hz CDG updates present independently of an unchanged video layer (`D3D11RendererPresentationServices.cs:296-350`) — a genuinely good design VLC has no equivalent for; and revision-tracked retained-layer skip logic (`D3D11MediaCompositionPresenter.cs:358-413`).

---

## 7. Robustness and Production Readiness

This is where the honest answer is uncomfortable: the gap to VLC is not features, it is **operability**.

### 7.1 Hard gaps (all verified by absence)

1. **No error channel.** Every open failure returns bare `false` with no reason (`AudioEngine.cs:237-241`; `VideoDecoder.cs:87-118`; `VideoAudioEngine.cs:189-214`). No error event exists on the public API (grep: zero `Error|Failed|Fault` events). VLC surfaces `EncounteredError` plus log callbacks. A host cannot distinguish "missing codec" from "file locked" from "corrupt container."
2. **No device-lost/TDR recovery.** No `DXGI_ERROR_DEVICE_REMOVED` handling anywhere; `Present` failures are logged and swallowed (`D3D11SwapChainPresenter.cs:43-46`). After a driver update/TDR the engine logs failures forever. VLC detects device-removed and rebuilds the vout. For an app that runs all night at a venue, this is the #1 field-failure mode.
3. **No audio device hot-plug handling.** No device enumeration, no `BASS_CONFIG_DEV_DEFAULT`, no reinit on default-device change (grep-confirmed). Unplugging a USB interface mid-show silently kills audio.
4. **Mid-stream hard I/O errors silently stall video.** Non-EOF `av_read_frame` failures make the decode loop idle-retry forever with no event (`VideoDecoder.cs:126-136`; `VideoPipeline.cs:1094-1106`). Truncated files usually hit EOF and end "normally," but a network dropout freezes the frame with no notification.
5. **A crashed decode/presentation loop dies silently** — `PlaybackScheduler.RunLoop` catches all and writes to `Debug` only (`PlaybackScheduler.cs:182-196`).
6. **Debug-only logging.** No `ILogger`, no routable diagnostics; `Debug.WriteLine` vanishes in Release. The diagnostic *content* is excellent (per-120-frame presentation stats, `VideoPipeline.cs:1736-1794`) but unreachable in production.
7. **Zero tests, zero CI.** Confirmed by repo AGENTS.md. Highly testable pure logic exists (CDG state machine, seek acceptance, timing math, clock estimator) and none of it is covered.

### 7.2 Where production discipline is already good

- Careful teardown ordering, including the classic DXGI resize trap (output views released before `ResizeBuffers`, `D3D11NativeHwndSwapChainBackend.cs:130-144`).
- Event subscription hygiene (matched +=/−= throughout the coordinator).
- Real NuGet packaging: deterministic build, symbols, Release-warnings-as-errors.
- Clean framework-neutral API (`Api/IMediaControlSession.cs`) with sync+async transport pairs — though it lacks `StateChanged`/`PositionChanged`/error events, forcing hosts to poll.
- Documented architecture with honest inline rationale (e.g., seek-pump comments, `VideoPipeline.cs:32-35`).

### 7.3 Feature coverage vs VLC (facts, briefly — noted per scope)

Behind: no subtitles, no multi-track audio selection, no chapters, no network/streaming input, no gapless/crossfade/mixer, no device selection API. Ahead: full CDG decoder with complete instruction coverage (`CdgStateMachine.cs:109-120`), audio+CDG pairing including inside archives, pitch/key change (±60 semitones) and tempo as first-class controls, cached two-stage loudness normalization (`AudioEngine.cs:549-610`), archive-native playback, sidecar background video layers. For karaoke, the "ahead" list is the whole product.

---

## 8. Biggest Improvements Toward the Next Milestone

Milestone framing: **"1.0-beta: a karaoke engine a venue can run unattended all night."** That target dictates the priority order — resilience and sync correctness before features. Ranked by (field impact × architectural leverage) ÷ risk:

### P1 — Error surfacing + resilience events (highest value per line of code)
Add `MediaError` event + result objects carrying reason codes to `IMediaControlSession`; raise on open failure, decoder stall (non-EOF read failures past a threshold), scheduler loop crash, and BASS failures. Wrap the BASS callback event raise in try/catch (`AudioEngine.cs:475-487`).
**Why first:** every other robustness fix is invisible without it; it is additive (no behavior change for existing hosts); and it converts today's "silent freeze" failure modes (§7.1 items 4–5) into recoverable host actions. VLC's single biggest operational advantage is that failures are *reportable*.

### P2 — GPU device-lost recovery + reuse decoder device across Opens
Check `GetDeviceRemovedReason` on present failure; tear down and rebuild device/compositor/swapchains, re-attach surfaces. Simultaneously, cache and reuse one FFmpeg D3D11 device across `VideoDecoder.Open` calls instead of recreating per file (`VideoDecoder.cs:425`), which both eliminates the per-track GPU rebuild (§6 item 8) and makes the recovery path the *same code* as the open path — one mechanism, two wins.
**Why:** TDRs and driver updates are the top real-world crash class for D3D11 apps left running for hours; and per-song GPU churn directly degrades the core karaoke workflow (rapid track transitions).

### P3 — Audio output latency feedback into the clock
Query BASS playback buffer level/latency and subtract it in `AudioPresentationClock`/the position snapshot; add `BASS_CONFIG_DEV_DEFAULT` + device-change reinit while in the same file.
**Why:** this is the difference between "sync is right on my sound card" and "sync is right on Bluetooth/HDMI/USB interfaces." It is a bounded change (one offset term in an already drift-corrected clock) with outsized correctness payoff, and it fixes the only *unobservable* error in the current sync model (§3.3).

### P4 — Move `Present` out of the lock scope
Restructure `PresentFrame` → coordinator so frame selection happens under `_presentationLock` but the blt/Present executes after release (snapshot the frame ref, present, then return the previous frame to the pool). Add a GPU fence/event query before `av_frame_free` of hardware frames.
**Why:** removes the engine's worst contention point (§5.2.1) — up to 16.7 ms of transport/CDG stall per frame — and closes the frame-recycle race (§2.2) in the same seam. Prerequisite for any high-refresh work.

### P5 — Display-locked pacing (waitable swapchain)
Create swapchains with `FRAME_LATENCY_WAITABLE_OBJECT`, wait on it in the presentation loop instead of the free-running timer (keep the timer as fallback for the DComp `Present(0)` path), and raise the 120 Hz tick clamp (`VideoPresentationTiming.cs:16`).
**Why:** eliminates the timer-vs-refresh beat that causes periodic judder (§3.3) — the most *visible* quality gap vs VLC — and does so by deleting complexity (the adaptive-lead controller can shrink once pacing is display-locked). Depends on P4.

### P6 — Single shared demuxer for video files
One `AVFormatContext` feeding video and audio packet queues (the decoder threads already exist; only the read stage merges).
**Why last among the big items:** it halves file I/O and unifies seek, and it is the enabling prerequisite for subtitles/multi-track/chapters later — but it is the highest-regression-risk change and current correctness holds without it. Do it when the resilience layer (P1/P2) exists to catch regressions in the field.

### Continuous — tests for the pure logic
CDG state machine (deterministic replay makes golden-frame tests trivial), `AudioPresentationClock` drift/rate math, seek acceptance rules, `VideoPresentationTiming`, end-of-media tolerance. No mocking of BASS/FFmpeg needed for any of these.
**Why:** P1–P6 all touch timing-sensitive code; shipping them without a regression net on the *testable* 30% of the engine multiplies risk for no savings. This is also the cheapest credibility gain toward "production ready."

**Explicitly deprioritized** (honest, against instinct): network input, subtitles, gapless/mixer, and format breadth. They are VLC's territory, they don't serve the karaoke milestone, and each competes for the same engineering budget as the resilience work that actually blocks unattended operation.

---

## 9. Bottom Line

FatzBombMedia.MediaControl is **not** a worse VLC — it is a differently-shaped engine that beats VLC decisively in its niche (CDG rendering architecture, key/tempo control, normalization, scrub UX, archive-native karaoke workflows) while trailing it in exactly the places 20 years of field deployment forged VLC: unified clocking with output-latency feedback, display-locked presentation, device-failure recovery, error reporting, and automated verification. The internal engineering discipline (bounded queues, pooled buffers, generation-guarded teardown, zero-copy HW path) means the remaining gaps are *addressable* rather than foundational. Execute P1–P4 and this is a legitimately production-grade karaoke engine; P5–P6 close most of the remaining visible-quality and structural distance to VLC's core.

---

*All FatzBombMedia citations verified against source on 2026-07-03. VLC comparisons reflect libvlccore's documented architecture (input thread / es_out / decoder FIFOs / vlc_clock / vout display-time scheduling) and are not side-by-side measurements. No benchmarks exist in this repo; performance statements are architectural analysis.*
