# Stage Update

Stage: 022A.4 - CD+G seek replay checkpoints; seek-bar drags off the UI thread
Date: 2026-10-18

## Status: IMPLEMENTED - owner smoke passed 2026-10-18 (with the follow-up, capture `cdg-3`)

Owner capture `cdg-2` (60 s, CD+G and video scrubbing; everything looked right):
- CD+G: the render loop no longer waits on the CD+G render lock during seeks (405 ms in the
  `cdg` baseline, none now); the CD+G seek worker's waits fell from 67 ms to about 2 ms.
- Video drags (regression, fixed in a follow-up commit): each coalesced drag seek ran the full
  seek path on the control queue, including `RefreshCurrentPosition`, which renders and
  presents the current frame (a vsync present) while holding the video pipeline lock. The
  video seek pump, rendering the scrub preview, waited 4.3 s in total (12 ms average, up to
  50 ms) and the refresh waited 2.3 s for it. Drag seeks (a `SeekAsync` during a scrub) now
  skip that refresh, as `Controls.Seek` did before this slice; the seek pumps present the
  preview themselves. `SeekAsync` outside a scrub and the release `Seek` refresh as before.
  Test: `SeekAsync_DuringAScrub_LeavesThePreviewToTheSeekPump` (mutation-checked).

Owner capture `cdg-3` (same mix, with the follow-up): total lock waits 622 ms (`cdg`) ->
8,054 ms (`cdg-2`) -> 556 ms. The seek-pump waits on the drag refresh are gone; the render
loop waits 1 ms on the CD+G lock and the CD+G seek worker 0.03 ms. Left, and accepted: on
the control-queue worker a drag seek's position read (`SeekOperation` -> `Position` ->
`VideoAudioEngine.Position`) occasionally waits for the video seek pump's audio lock (6 waits,
267 ms in total, up to 123 ms). No UI or render thread waits on it, and newer drag targets
coalesce meanwhile.

Baseline (owner `cdg` capture, 60 s of scrubbing a CD+G song):
- The CD+G render loop waited on the CD+G engine's render lock 101 times, 405 ms in total, up
  to 15.5 ms at a time, while the seek worker held it replaying packets.
- `CdgStateMachine.ProcessPacket` was the hottest code in the trace, on both threads: a
  backward seek replayed every packet from the start of the song under the lock (about
  72,000 for 4 minutes), and the render loop then rebuilt again.
- Seek-bar drags called `Controls.Seek` directly on the UI thread for every mouse move
  (about 400 samples in the capture), outside the engine's control queue.

Delivered:

- CD+G replay checkpoints. Loading a CD+G file replays it once on a scratch state machine,
  outside the render lock, and keeps a copy of the packet-driven state (pixel indices, colour
  table, border and background indices; about 65 KB) every 10 s of packets
  (`CheckpointIntervalPackets`): about 1.6 MB and 45 ms for a 4-minute song. A render that has
  to move back, or forward past a checkpoint, restores the nearest checkpoint at or before the
  target and replays at most 10 s of packets; otherwise replay continues from the current
  state, so a forced (authoritative) render at the current position replays nothing. The
  host's presentation settings (background opacity and override) are not part of a checkpoint
  and stay as set. Packets, checkpoints and replay state are swapped and reset in one lock, so
  no render runs a new file's packets over the previous file's state.
  Measured here (4-minute random stream): first jump to 3:55 on a fresh load 1.6 ms (was about
  50 ms); backward seeks 0.7 ms on average, 1.6 ms at worst.
- `IMediaControlSession.SeekAsync(double)`: the same seek as `Seek`, run on the Core control
  queue in order with other control operations. The newest request wins: one made while another
  is still waiting to run replaces its target, and both callers get that seek's result, so any
  host can call it for every mouse move. Default interface body seeks synchronously.
- Player: seek-bar drags call `SeekAsync` for every move, so the UI thread does no seek work.
  The release seek stays synchronous; the queue runs it after any drag seek already queued.
- Tests: `CdgEngineCheckpointTests` (5: random 83 s stream shaped like a real disc, with many
  backward and forward seeks and a forced rebuild; a first seek far into the song; the
  background override surviving a restore; reloading another file), each compared with an
  independent replay from the first packet on a bare `CdgStateMachine` (pixels, palette,
  background index, wash). Mutation-checked: resuming replay at the wrong packet, skipping the
  colour restore, and keeping the previous file's checkpoints all fail them.
  `MediaControlPlayerTests`: `SeekAsync` runs in order with a synchronous `Seek`, and requests
  waiting to run collapse to the newest.

## Decisions made without the owner

- Checkpoints instead of replaying into a separate state machine and swapping it in: replay is
  now bounded to 10 s of packets, so the lock hold is short without a second state machine or
  a swap protocol. Built at load rather than as playback first passes each point, so a first
  seek into an unplayed part is bounded too, at the cost of about 45 ms of load time.
- Drag seeks go through the control queue with the seek bookkeeping (`SeekCompleted`) but
  without the presentation refresh, which the seek pumps already do (see the `cdg-2` result
  above); the release seek keeps the refresh.
- CD+G snapshot arrays are not pooled. CD+G playback allocates about 5 MB/s (a gen-0 collection
  about every 3 s, sub-millisecond), and snapshots are immutable and shared across threads and
  the composition cache for an unknown time (018A's ownership decision); reusing their arrays
  could show a half-overwritten frame.

## Manual smoke (Windows, native DLLs)

```
cd D:\development\FatzBomb\FatzVideo
git checkout claude/wonderful-heisenberg-9x3p6a
git pull
dotnet run --project FatzBomb.MediaControl.Player -c Release
```

1. A CD+G song: play past 1-2 minutes, then click-seek backwards and forwards several times,
   and drag the seek bar back and forth: lyrics redraw correctly at each position (the right
   page, colours and background), with no hitch when releasing.
2. Change the CD+G background opacity (BG slider), then seek backwards: the opacity stays.
3. Video: drag the seek bar quickly for 10-20 s; the window stays responsive and playback
   resumes from where you release.
4. Optional: record `cdg-2` while scrubbing a CD+G song, as for the `cdg` baseline:
   `pwsh scripts\capture-thread-baseline.ps1 -Label cdg-2 -DurationSeconds 60`, and send the
   `.nettrace` and `.counters.csv`; render-lock waits should drop from hundreds of ms.
