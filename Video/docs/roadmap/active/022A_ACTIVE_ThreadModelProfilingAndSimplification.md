# Active Stage 022A - Thread Model Profiling and Simplification

Prepared: 2026-10-16
Status: Slice 1 (thread-ownership map from code, repeatable capture tooling) implemented.
The measured baseline needs Windows with native DLLs and is the owner's next step; no
thread behaviour was changed.

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
  - BASS mixer/output threads (native)  - "VideoDecode" LongRunning thread    - "CoreNativePresentation" LongRunning
  - SYNC callbacks: End/Position        - "VideoPresentation" LongRunning       thread, AboveNormal; runs only for
    (mixtime) -> MediaEnded on a BASS     thread, AboveNormal; MediaLoopPacer    CDG or audio+subtitles (60 Hz-class
    thread; MetadataReceived/OggChange    + IPrecisionWaiter                     CdgPresentationInterval, MediaLoopPacer)
  - network monitor System.Threading.   - "VideoSeek" dedicated thread,       - "DeviceLostRecovery" LongRunning
    Timer (250 ms, network sources)       latest-wins pump                      (only while recovering)
  - analysis: Task.Run (peak, silence,  - "DecoderDeviceLostRecovery"         - renderer calls are synchronous on
    prepared-next standalone decode)      LongRunning (only while recovering)   the calling thread (no renderer threads)
                                        - "BackgroundVideoPresentation"
  CDG (CdgPipeline)                       LongRunning (sidecar video)
  - "CdgSeek" thread (shared SeekPump)  Platform
  - packet decode on the presentation   - Windows "PollingAudioDeviceMonitor" (1 s poll)
    loop / seek thread                  - Linux "LinuxAudioDeviceMonitor" (poll)
  PlaybackPositionNotifier: PeriodicTimer on the pool (250 ms) while playing
  MediaFile play-on-load and faded prepared-next transitions: Task.Run hops off the queue
```

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

## Simplification Candidates (only after the baseline)

- None proven yet. The presentation loop already starts only for CDG or audio+subtitle
  content; seek threads park on a signal when idle. Candidates will be chosen from the
  captured wakeup/contention data, one ownership boundary per slice.

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
