# Stage Update

Stage: 022A.2 - Remove the measured per-frame costs
Date: 2026-10-18

## Status: IMPLEMENTED - owner smoke passed 2026-10-18

Baseline: captured by the owner on Windows on 2026-10-17 with
`scripts/capture-thread-baseline.ps1` (idle, audio, cdg, video-hw, video-sw); results and
sources are in the stage file's "Measured Baseline". The raw captures are not committed.

Delivered:

- `SoftwareCompositionRenderer.Render(frame, width, height, ref buffer)` (internal) renders
  into a reused buffer. `SoftwareRendererBackend` keeps one: a present takes it out
  atomically and puts it back afterwards (no lock across the sink call; a concurrent present
  renders into its own buffer), and cleanup/dispose drop it. The public overload is unchanged.
- `VideoAudioEngine.IsOpen` no longer takes `_sync`: it reads `_openPublished` (set after a
  successful open, cleared first thing in close) and the demuxer's lock-free open state.
- `VideoPipeline` resolves the display interval (host > renderer > platform) at most every
  100 ms instead of several times per frame, so neither the renderer's cache nor DWM is
  queried on the per-frame path, and one frame sees one rate.
- Tests: reused-buffer and independent-output cases in `SoftwareCompositionRendererTests`.

## Decisions made without the owner

- Kept the public `SoftwareCompositionRenderer.Render` returning a fresh buffer, since its
  output is documented as immutable and tests compare several outputs.
- `IsOpen` may now report false a moment earlier during close and true a moment later during
  open than before (it no longer waits for an in-flight open/close); every caller treats it
  as a snapshot already.
- Left the CDG, thread-churn, and Player seek candidates for later slices (stage file).

## Manual smoke (Windows, native DLLs)

1. Software mode (`-- --software`): play a 1080p video, seek, pause, resize the window,
   loop: the picture is identical to before (no tearing, no stale frames after resize).
2. Re-record the software baseline while doing the same and compare the counters:
   `pwsh scripts\capture-thread-baseline.ps1 -Label video-sw-2 -DurationSeconds 60`.
   Expected: allocation far below the previous ~109 MB/s while playing, fewer gen-0 GCs.
3. Hardware mode: play a video with audio, seek, loop, stop, open another file: audio and
   video stay in sync; closing and reopening works.

## Review follow-up (same day, local code review)

Fixed: the first version held a render lock across the sink call, which could deadlock
against a teardown on the UI thread with a sink that marshals synchronously, serialized video
and CDG presents, and could keep a buffer alive after dispose; the buffer is now taken and
returned atomically. The DWM-only cache in the Windows platform was replaced by the memo in
`VideoPipeline`, which also covers the renderer's per-monitor rate. `SoftwareRenderOutput`'s
documentation no longer claims every output is immutable.

Not changed: `IsOpen` reports false while another thread re-opens the audio engine, where it
used to block that caller until the open finished (the presentation loop is stopped during
an open, and blocking it was the cost being removed); its demuxer read is the same plain read
the locked version used.

## Second capture (owner, 2026-10-18)

`video-sw-2` after this slice: steady software playback fell from about 109 MB/s to about
55 MB/s, which is exactly half. The rest was one more surface-sized array per frame, in the
WPF composited sink (`WpfCompositedSurfaceHost.PresentFrame` allocated `_pendingPixels` every
frame because publishing set it to null). Bursts of 150-425 MB/s were the same allocation
while the window was maximized (a 6-8x larger surface), not a new cost. Fixed by keeping the
published array as a spare for the next frame, the pattern the Avalonia sink uses; a spare
more than twice the needed size (left over from a maximize) is dropped
(`WpfCompositedSurfaceHostTests`). Expected after this: about the hardware-mode rate
(~1 MB/s). Re-record `video-sw-3` to confirm.

The same capture round found ENG-015 (secondary window with hardware video), fixed separately.

## Third capture (owner, 2026-10-18)

`video-sw-3` (software mode, seeks, one maximize): steady playback about 0.5 MB/s (from about
109 MB/s in the baseline and 55 MB/s after the first fix), gen-0 collections essentially gone.
Remaining spikes match the workflow: open (107 MB), seeks (9-17 MB/s for a second or two),
the maximize (41 MB).

