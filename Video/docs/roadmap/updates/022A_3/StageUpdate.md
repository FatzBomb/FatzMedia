# Stage Update

Stage: 022A.3 - Reuse scheduler threads across restarts
Date: 2026-10-18

## Status: IMPLEMENTED - manual Player smoke pending

Baseline: every `VideoPipeline.SeekCore` restarts the decode and presentation loops, and
`PlaybackScheduler.Start` created a new LongRunning thread each time: about 55 + 55 OS
threads per minute of scrubbing in the owner's video-sw capture (38 + 38 in video-hw).

Delivered:

- `PlaybackScheduler` keeps one dedicated worker thread (named `FatzBombMedia.<name>`, the
  presentation schedulers at AboveNormal priority as before). `Start` cancels the current
  loop and queues the new one; the worker runs it as soon as the old loop returns. A start
  replaced before it ran never runs. The worker parks between runs and exits after 30 s
  without work, or on `Dispose`; the next `Start` creates a new one.
- `StopAndWait` waits for the running loop to return (up to 2 s, with the same warning as
  before); called from the loop itself it only cancels. A loop can still restart its own
  scheduler.
- Same public surface; no caller changed (the video pipeline, presentation coordinator,
  background video, and device-lost recovery schedulers all use it).
- A loop that does not return within the `StopAndWait` timeout (a native call ignoring
  cancellation) keeps its thread; the scheduler abandons that worker, warns, and the next
  `Start` creates a new one, so playback is never stuck behind a hung loop (the old code got
  this for free by starting a new thread every time).
- Tests: `PlaybackSchedulerTests` (9): one thread across 20 restarts, no overlap, wait,
  self-restart, replaced start skipped, failing loop, idle exit, hung loop, dispose. Run 20
  times in a row here without a failure.

## Decisions made without the owner

- Restarted loops on one scheduler no longer overlap: the old code started the new task
  while the cancelled one was still finishing its iteration. Every loop already exits on
  cancellation or a generation change, so the new loop starts a few milliseconds later at
  most (the end of the old loop's iteration), and two presentation loops can no longer
  present at once during a seek.
- 30 s idle exit rather than a permanent thread, so rarely used schedulers (device-lost
  recovery) do not keep a thread parked for the whole session.

## Manual smoke (Windows, native DLLs)

1. Hardware video: drag the seek bar for 10-20 s, click-seek, pause/resume, loop, stop,
   open another file: playback and seeking behave as before (no stalls after a seek).
2. Software mode (`-- --software`): the same.
3. CD+G with a video background and an audio file with subtitles: seek and play.
4. Optional, to confirm the fix: record `video-hw-2` while scrubbing:
   `pwsh scripts\capture-thread-baseline.ps1 -Label video-hw-2 -DurationSeconds 60`
   and send the `.nettrace`; thread creations during seeks should drop from dozens to a few.

## Review follow-up (same day, local code review)

Fixed in the first draft: a hung loop blocked later starts (now abandoned after the timeout);
cancellation ran under the scheduler lock, so token callbacks could deadlock (now outside);
a throwing diagnostic subscriber could end the process from the raw worker thread (caught);
`Dispose` set its flag after stopping, so a late start could run after disposal (now first,
and the worker never takes a run once disposed); a failed thread start left the scheduler
with a dead worker (recorded only after it starts); the worker's wake event and replaced
pending runs were never released; the idle-exit test relied on a fixed sleep (now polls).

Not changed: `SeekPump` has a similar dedicated-thread loop; the two have different
semantics (latest-wins requests with preview/settle vs. cancellable long-running loops), so
they stay separate.

