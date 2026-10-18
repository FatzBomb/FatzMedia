# Completed Stage 022A - Thread Model Profiling and Simplification

Prepared: 2026-10-16
Status: Completed 2026-10-18. Slices 1-4 (thread map and capture tooling; per-frame buffers,
audio lock and rate queries removed; reused scheduler threads; CD+G seek checkpoints and
off-UI-thread drag seeks); owner smoke passed 2026-10-18 (`updates/022A_1` to
`updates/022A_4`). A fresh capture before more simplification is listed in
`active/041A_ACTIVE_EngineInternalFollowUps.md`.

## Thread Ownership Map (pinned, from code)

```
Host UI thread
  MediaControlPlayer API ──enqueue──> MediaControlOperationQueue worker ("MediaControl";
  (sync Play/Pause/Stop/Dispose block       async pool worker, one op at a time; inline
   here; async variants do not)             re-entry on the worker; host events raised
                                            here via HostEventDispatcher)
                                               │
          ┌────────────────────────────────────┼─────────────────────────────────────┐
          v                                    v                                     v
  Audio (BASS)                          Video (VideoPipeline)                 Presentation
  - BASS mixer/output threads (native)  - "VideoDecode" dedicated thread    - "CoreNativePresentation" dedicated
  - SYNC callbacks: End/Position        - "VideoPresentation" dedicated       thread, AboveNormal; runs only for
    (mixtime) -> MediaEnded on a BASS     thread, AboveNormal; MediaLoopPacer    CDG or audio+subtitles (60 Hz-class
    thread; MetadataReceived/OggChange    + IPrecisionWaiter                     CdgPresentationInterval, MediaLoopPacer)
  - network monitor System.Threading.   - "VideoSeek" dedicated thread,       - "DeviceLostRecovery" dedicated
    Timer (250 ms, network sources)       latest-wins pump                      (only while recovering)
  - analysis: Task.Run (peak, silence,  - "DecoderDeviceLostRecovery"         - renderer calls are synchronous on
    prepared-next standalone decode)      dedicated (only while recovering)   the calling thread (no renderer threads)
                                        - "BackgroundVideoPresentation"
  CDG (CdgPipeline)                       dedicated (sidecar video)
  - "CdgSeek" thread (shared SeekPump)  Platform
  - packet decode on the presentation   - Windows "PollingAudioDeviceMonitor" (1 s poll)
    loop / seek thread                  - Linux "LinuxAudioDeviceMonitor" (poll)
  PlaybackPositionNotifier: PeriodicTimer on the pool (250 ms) while playing
  MediaFile play-on-load and faded prepared-next transitions: Task.Run hops off the queue
```

Every `PlaybackScheduler` ("VideoDecode", "VideoPresentation", "CoreNativePresentation",
"BackgroundVideoPresentation", the device-lost recovery schedulers) owns one reused worker
thread since slice 3: restarts run on it, and it exits after 30 s idle.

Synchronization: 26 source files in Core, Windows, and Direct3D11 own a `System.Threading.Lock`; the central ones are the
operation queue, `MediaPresentationCoordinator._lock` (composition state) and
`_cdgSnapshotGate`, `CdgEngine._renderLock` / `CdgStateMachine._lock`, the `SeekPump` gate
(one instance each for video and CDG since ENG-013), and `PlaybackScheduler._lock`. Cancellation: every `PlaybackScheduler`
loop takes a `CancellationToken` and `StopAndWait` waits up to 2 s; seek threads use a
shutdown flag + signal; timers are disposed with their owner.

## Capture Tooling

`scripts/capture-thread-baseline.ps1 -Label <workflow> [-DurationSeconds 60]` attaches to
the running Player and writes `artifacts/thread-baseline/<label>.counters.csv`
(CPU, thread-pool threads/queue/completions, lock contention, gen-0 GCs, allocation rate,
1 s resolution) and `<label>.nettrace` (sampled thread stacks ~100 Hz + runtime
Contention/Threading events). It installs `dotnet-counters`/`dotnet-trace` as global tools
when missing and fails loudly if either tool fails. Validated on Linux against a live
.NET process (all 7 counters present, trace written).

## Baseline Workflows (to capture on Windows)

| Label | Workflow (one capture each) |
|---|---|
| `audio` | open MP3, play 20 s, seek x5, pause 5 s, play, stop, close |
| `cdg` | open MP3+G, play 30 s, seek x3, pause, resize window, play, stop |
| `video-hw` | open 1080p MP4 (hardware decode), play 20 s, seek x5, loop once, stop, close |
| `video-sw` | same with `--software` |
| `idle` | Player open, nothing loaded, 60 s |

## Measured Baseline (Windows, 2026-10-17, Player Release, 60 s each)

| Workflow | CPU avg | Allocation | Gen-0 GCs | Lock contentions |
|---|---|---|---|---|
| idle | 0.3% | 0.04 MB/s | 0 | 0 |
| audio | 0.9% | 1.1 MB/s | 5 | 2 (open only) |
| cdg | 1.3% | 4.8 MB/s | 22 | 230 (622 ms) |
| video-hw | 1.0% | 1.1 MB/s | 5 | 291 (106 ms) |
| video-sw | 3.6% | 109 MB/s while playing | ~60 | 279 (153 ms) |

Sources, from the sampled stacks and contention events:
- video-sw allocation: `SoftwareCompositionRenderer.Render` allocated a new surface-sized
  BGRA buffer for every presented frame (Render samples track the allocation counter,
  r = 0.95).
- Video contention: the presentation loop read `VideoPipeline.HasAudio` ->
  `VideoAudioEngine.IsOpen`, which took the audio engine's `_sync` every frame while the
  decode thread held it in `DecodeAndPushAudio` (video-sw 221, video-hw 158 events; cheap).
- `DwmCompositionTiming.GetRefreshIntervalSeconds` ran on the presentation thread several
  times per frame (145 samples in video-hw).
- cdg contention: `CdgEngine._renderLock` between the presentation loop and the CDG seek
  replay (101 events, up to 15.5 ms, during seeks only).
- cdg allocation: `CdgStateMachine.CreateSnapshot` copies the 64.8 KB index array per
  presented CDG frame (75 Hz = 4.9 MB/s).
- Thread churn: each `VideoPipeline.SeekCore` restarts the decode and presentation loops on
  new LongRunning threads (about 55 + 55 OS threads per minute of scrubbing).
- The Player's seek-bar drag calls `MediaControls.Seek` on the UI thread (BASS seek work).

## Slice 2 (implemented)

- The software backend renders into one reused buffer, taken and returned atomically per
  present (the sink copies the span before returning); the public `Render` overload still
  returns independent outputs.
- `VideoAudioEngine.IsOpen` reads a published flag (set at the end of a successful open,
  cleared at the start of close) plus the demuxer's lock-free state, instead of `_sync`.
- `VideoPipeline` resolves the display interval at most every 100 ms instead of several times
  per frame (DWM is also only the fallback since 017A slice 3).

## Slice 3 (implemented, owner smoke passed 2026-10-18)

`PlaybackScheduler` runs its loops on one reused worker thread instead of a new LongRunning
thread per `Start`. Starting a loop cancels the current one; the new loop runs on the same
thread once the old one returns, so loops on one scheduler no longer overlap during a
restart. The worker parks between runs, exits after 30 s idle or on dispose, and is
recreated on the next start. The public surface (`Start`, `Stop`, `StopAndWait`, `Dispose`)
is unchanged, so no caller changed (`updates/022A_3`).

## Slice 4 (implemented, owner smoke passed 2026-10-18)

CD+G seeks restore the nearest replay checkpoint (every 10 s of packets, built once at load
off the render lock) instead of replaying from the first packet under the lock, so any seek
replays at most 10 s of packets (about 1 ms against 50 ms for a 4-minute song). Seek-bar drags
go through the new `SeekAsync` on the Core control queue, where the newest waiting request
wins, instead of running `Controls.Seek` on the UI thread. Snapshot arrays stay unpooled (5 MB/s,
gen-0 only; snapshots are shared and immutable per 018A) (`updates/022A_4`).

## Simplification Candidates (remaining, one per slice)

None from the baseline captures. Re-capture (`cdg-2`, `video-hw-2`) before choosing more.

## Acceptance Criteria (slice 1)

- Thread-ownership map recorded from code (above).
- Capture tooling runs end to end and fails on tool errors.
- Workflow table defines the baseline set; no thread behaviour changed.

## Why This Is Worth Doing

A documented and measured thread model improves maintainability even when no immediate
failure exists. It can expose unnecessary wakeups, lock contention, blocking, and
unclear ownership while creating a baseline for future scheduler changes.

## Recommended Direction

- Document every long-lived thread, scheduler, callback thread, lock, and cancellation path.
- Capture CPU, wakeup, context-switch, and lock-contention traces during real workflows.
- Simplify one ownership boundary at a time when the new model is easier to prove correct.
- Keep thread count as an observation, not the success metric.

## Recommended Immediate Action

Capture an ETW or `dotnet-trace` baseline for open, play, seek, pause, loop, stop, and
close across audio, CDG, and video. Pair the trace with a thread-ownership diagram.

## Must Preserve

- Decode/presentation separation.
- Latest-wins seek behavior and operation ordering.
- CDG/subtitle coordination.
- Audio/video synchronization.
- Cancellation and disposal safety.

## Explicit Non-Goals

- Reducing thread count for its own sake.
- Merging independent loops into one oversized scheduler.
- Replacing measured native waits with busy loops.
- Combining thread changes with unrelated media behavior changes.
