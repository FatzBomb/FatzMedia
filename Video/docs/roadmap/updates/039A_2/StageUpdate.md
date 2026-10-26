# Stage Update

Stage: 039A.2 - Network video: read-ahead
Date: 2026-10-25

## Status: IMPLEMENTED - owner smoke passed 2026-10-26 except steps 4, 5 and 7

Owner's scope: forward read-ahead only. No back-buffer and no whole-file cache: after a seek
outside the buffered range the player rebuffers from the new position.

Delivered:

- **Read-ahead** (`Core/Media/NetworkReadAhead`, queues in the pure `ReadAheadBuffer<T>`). A
  `SharedDemuxer` opened with `Open(url, FfmpegNetworkOptions)` starts a reader thread with the
  first read or seek. The reader owns every network read and seek of the format context and
  demuxes the registered streams (picture, and sound when it plays) into per-stream queues, with
  an end marker at the end of the source. It stops at the duration cap (measured on the
  picture's packets, or the sound's when there is no picture) or the byte cap, whichever comes
  first, and resumes as packets are taken. It never stops while a consumer is starved (found its
  queue empty since the last packet for it), so a consumer cannot wait on a full buffer; twice
  the byte cap is a hard limit (a Warning diagnostic). Local files keep the inline cursor.
- **Host option**: `MediaPlayerConfiguration.NetworkReadAhead` (`NetworkReadAheadPolicy`, next
  to `NetworkRetry` and `NetworkTls`, the same nullable-record shape): `MaxDuration` (default
  20 s, 1 s..10 min), `MaxBytes` (default 64 MB, 1 MB..1 GB), `ResumeDuration` (default 2 s,
  0.1 s..`MaxDuration`). Passed to the demuxer in `FfmpegNetworkOptions.ReadAhead`.
- **Buffering from the buffer's fill**: `NetworkBufferingMonitor` samples the read-ahead every
  250 ms. A consumer starved for 0.5 s while playing starts buffering (the buffer ran dry); it
  ends once `ResumeDuration` is buffered, a quarter of `MaxBytes` is held, the buffer is full,
  or the source ended. The reported fill is the progress towards that. The Audio-mode soundtrack
  uses the same rule (its own cursor has its own read-ahead).
- **One read for picture and sound**: network video's `VideoAudioEngine` is built on the
  picture's `SharedDemuxer` (the shared-cursor mode that already existed) and takes its packets
  from the read-ahead. `SeekCore`'s container seek repositions both; the audio's own seek only
  re-arms its decoder. A gapless loop wrap keeps the sound's tail packets and end marker and
  appends the next lap after them. An audio engine that closes or fails to open unregisters its
  stream, so its packets are not kept. Local video keeps audio on its own cursor (the AV1 + E-AC-3
  startup offset). The `_ioGate`/`_sync` order is unchanged: reads wait under `_ioGate` only.
- **Seek inside the buffer**: a forward seek to a buffered target drops the picture's packets
  up to the keyframe at or before it and the sound's packets that end before that keyframe, and
  decodes on to the target. It is refused (and goes to the network) when the target is not
  buffered yet, its keyframe was already taken, or the sound already took the target's packets.
- **Any other seek** empties the queues and hands the seek to the reader. The reader gives up its
  read in progress at once through an interrupt condition on the format context
  (`FfmpegInterrupt.SetOperationCancellation`: no timing window, unlike a timestamp), then seeks.
  `SharedDemuxer.Seek` returns at once; the next reads wait for the new position. A failed reader
  seek is reported (`Network seek failed`, not for live sources) and reading goes on from where
  the cursor is.
- **One network seek per seek**: a lone network seek settles on its exact preview
  (`CanSettleOnPreview`: same target, same decoder, nothing repositioned since) instead of
  seeking again. The seek's decode budget and the adaptive pump interval no longer count the
  time spent waiting for packets (`NetworkReadAhead.WaitedTicksOnThisThread`); before, the
  preview gave up on the download and the settle downloaded the segment again.
- **Waits** (`NetworkReadAhead.WaitScope`): consumer reads wait for packets unless the thread
  limited them. The decode loop, the presentation loop and the paused render never wait
  (`NoWait`); network Stop waits at most 0.5 s for its first frame; a seek's waits give up when a
  newer seek, Pause or Stop is queued (`AbandonWaitsWhen`); the soundtrack pump's waits end with
  its cancellation (`CancelWaitsWith`). A wait that gives up returns `AVERROR_EXIT`, which every
  consumer already treats as "not the end". Close and Dispose abort the reader (the ENG-035
  interrupt) and join it before the format context closes.
- **ENG-035**: getters never wait on network I/O. The read-ahead's lock is a leaf held for queue
  work only; `Position`, the setters and the buffering sample never touch the network.

## Decisions made without the owner

- **Starvation, not an empty queue, starts buffering.** A consumer that finds its queue empty for
  0.5 s while playing starts it, so a momentary empty queue (a packet arriving a moment late)
  does not flap. Buffering stays an event, as in slice 1: the clock is not held while the buffer
  refills.
- **The playback loops never wait for packets.** With an empty queue the decode loop moves on to
  the other stream and comes back 10 ms later, so Pause and Stop never join a waiting thread, and
  a dry picture queue does not hold up the sound.
- **The seek API stays synchronous in shape but not in effect**: `SharedDemuxer.Seek` returns 0
  once the reader has the request. Callers ignored the result or only logged it; the reader logs
  a real failure.
- **Seek-inside-buffer needs the sound behind the target.** The sound consumer runs up to about
  4 s ahead of the picture (BASS's push buffer), so with BASS a forward seek shorter than that
  goes to the network (ENG-034 follow-up).
- **Audio mode** keeps its own cursor (no picture) and gains a read-ahead too; its seek gives up
  the pump's wait first, so a seek during a stall does not wait for the download.
- **Live HLS**: the read-ahead fills to the live edge; Stop's seek to 0 fails by design and is not
  reported.
- **Network Stop** shows the first frame only if the reader delivers it within 0.5 s.

## Measurements (Linux sandbox, localhost, FFmpeg 9 shared, no BASS, software decode)

Opt-in native tests, three runs (typical / range). "Before" is the base commit (57888e0) in the
same sandbox, with the same test added to it (not committed).

Seek latency (Seek call to `SeekCompleted`):

| | Before | After |
|---|---|---|
| Progressive MP4, forward seeks inside the buffer (4, 6, 8 s) | 14-20 ms, no request (FFmpeg read on) | 11-18 ms, no request |
| Progressive MP4, back to 1 s (outside) | 47-65 ms, 1 range request | 51-66 ms, 1 range request |
| HLS 1080p, 150 ms per segment: forward into buffered range (to 6 s; to 20 s in the variant test) | 314-318 ms, target segments fetched twice | 15-25 ms, no request |
| HLS 1080p, 150 ms per segment: outside, forward (to 23 s) | 305-314 ms, seg011 and seg012 fetched twice | 408-439 ms, fetched once |
| HLS 1080p, 150 ms per segment: outside, back (to 3 s) | 309-313 ms, fetched twice | 370-420 ms, fetched once |

A seek outside the buffer is about 80-120 ms slower on localhost: the preview now decodes to the
exact target frame and the pump interval (75 ms) runs before the settle, where before the preview
gave up after its 95 ms budget and the settle seeked again at once. It downloads half as much;
over a real network the download dominates.

Read-ahead (demuxer level, 1080p 4.47 Mb/s HLS variant with audio):
- Duration cap 6 s: stops at 6.00 s, 3.0 MB, after fetching seg002-seg004; nothing more is fetched
  while idle; taking 3 s of packets resumes it and it stops at the cap again.
- Byte cap 2 MB: stops at 2067 KB (one packet past the cap), 4.1 s.
- Default policy: 20.0 s ahead, 10.1 MB of packets; the process working set grew 2-15 MB across
  runs (open included; the packets are native memory). The default caps hold at most 64 MB (128 MB
  hard limit with a starved stream) per open source.
- Picture and sound read to the end from one read-ahead: HLS 902 picture and 1408 sound packets,
  each of the 16 segments of the chosen variant fetched once, no URL requested twice; progressive
  MP4 300 and 470 packets, 3 requests, all made by the open (before: a second cursor opened the
  file again).

Stalls and transport (player level, no BASS):
- 2 s server stall on a segment request with a 4 s read-ahead, at 3.5 s: no buffering event,
  longest gap between presented frames 34-50 ms (30 fps), 134 frames over 4.5 s.
- Every segment hung for 20 s once the 2 s buffer is short: buffering starts (`[True]`); Pause
  2 ms, Stop 501 ms (its 0.5 s wait for the first frame), Close 36-45 ms. Before, Pause and Stop
  joined a decode thread blocked in the read (up to the read timeout).
- A seek outside the buffer waiting for a hung segment: Pause 4-11 ms, Stop during a second such
  seek 411-414 ms, Close 89-100 ms.
- Close while the reader waits for a hung segment (paused after the load): 87-98 ms.
- Server cut at 45 % with a 3 s reconnect delay and a 1 s read-ahead: `[True, False]`, buffering
  ended 1.56-1.66 s after it started (slice 1's case; a 20 s read-ahead absorbs it).
- ENG-035 cases unchanged in outcome: a newer seek during a 10 s stalled segment settles in
  422-426 ms, during a stalled range request in 119-122 ms; Close during a stalled seek 99-102 ms;
  UI getters at most 3.7 ms.

## Verification

- Linux: `EnableWindowsTargeting=true dotnet build FatzBomb.MediaControl.sln -c Release`:
  0 warnings, 0 errors.
- `FatzBomb.MediaControl.Portable.Tests`: 1344 passed (20 new cases in `ReadAheadBufferTests`:
  accounting, caps, resume fill, end markers and loop wrap, starvation, seek-in-range decision
  and its refusals, flush, unregister, policy clamps, buffering hysteresis).
- `FatzBomb.MediaControl.Linux.Tests` with `FATZBOMB_LINUX_FFMPEG_DIR=/opt/fatzbomb-native/ffmpeg/lib`:
  90 passed, three clean runs. 11 new opt-in native cases
  (`LinuxNetworkVideoNativeTests.ReadAhead.cs`): duration cap, byte cap, default-policy memory,
  one download for picture and sound (HLS and MP4), 2 s stall without buffering or frame gap,
  seeks inside the buffer without a request and outside it fetching the target once (HLS),
  progressive forward seeks without a request, Pause/Stop/Close during a stall, Pause and Stop
  during a stalled seek, Close during a fill. `ServerDroppingMidStream_TriggersRetryAndBufferingEvents`
  now uses a 1 s read-ahead and a 3 s reconnect delay (a 20 s buffer absorbs slice 1's 1.5 s).
- `FatzBomb.MediaControl.Avalonia.Tests`: 33 passed. `FatzBomb.MediaControl.Rendering.Vulkan.Tests`:
  67 passed.
- `dotnet format FatzBomb.MediaControl.sln --verify-no-changes` (with `EnableWindowsTargeting=true`):
  clean. `check-core-boundary`, `check-adapter-boundary`, `check-wpf-boundary` and
  `check-presentation-parity` (pwsh): passed; no project reference changed.
- Not verified here: sound (no BASS): the shared audio cursor is verified at demuxer level
  (packets delivered from one download) and by reading; the gapless network loop with sound;
  Audio mode's soundtrack read-ahead; Windows (WPF, Direct3D11, D3D11VA); real internet links.

## Manual smoke (Windows, BASS and FFmpeg 9 in `native/`)

Use the Avalonia test player (`dotnet run --project FatzBomb.MediaControl.Player.Avalonia -c Release
-f net10.0-windows7.0 -- --renderer=direct3d11 --url=...`) or the WPF player (`--url=`). Keep a
network monitor open (Resource Monitor, Network tab, or `netstat -b`) to count connections.

1. **mux.dev HLS**: `--url=https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8`. Picture and sound
   in sync from the start. Resource Monitor shows one TCP connection to the host carrying the
   segments (before: two), and the download rate settles to about one variant's bitrate once
   about 20 s are buffered (a burst at start, then roughly real time).
2. **Seek inside the buffer**: after 20 s of playback, click 8-10 s ahead. The frame appears at
   once (tens of ms), sound follows in sync, and no new segment download starts for that range.
3. **Seek outside the buffer**: click far ahead (more than 25 s) and far back. Each seek settles
   in about one segment download (about a second on a normal connection), the log shows no
   error, the picture and sound resume in sync. With a network monitor: the target segment is
   downloaded once.
4. **Unplug the network for 3 s** (or disable the adapter) during steady playback of the mux.dev
   stream and of a progressive MP4: playback continues without a stall and no `Buffering=True`
   appears (20 s are buffered). Unplug for 30 s: playback runs on for about 20 s, then
   `Buffering=True`; plug back in: with `--net-retries=3` the stream recovers (`Buffering=False`
   once about 2 s are buffered); without it, `Network read failed` and the stream ends.
5. **Pause, Stop and Close during a stall**: unplug for 30 s and wait for `Buffering=True`, then
   Pause, Play, Stop and close the window: each responds at once (Stop within about 0.5 s).
   Also seek while unplugged and press Pause during that seek: Pause responds at once.
6. **Progressive MP4** from a range-capable server (`npx http-server <folder> -p 8080`):
   `--url=http://localhost:8080/clip.mp4`. One connection for picture and sound; forward seeks
   within the first 20 s make no new request; a seek back makes one range request. Loop on: the
   loop at the end is gapless (picture and sound).
7. **Audio mode**: `--mode=audio --url=<the MP4 or the mux.dev stream>`: the soundtrack plays,
   seeks, pauses; unplug for 3 s: no gap.
8. **Local files**: video playback, seek, loop and archives behave as before (no read-ahead);
   network audio (an internet radio station, 014A) is unchanged.

## Owner smoke results (2026-10-26, Windows, Avalonia, Software renderer, BASS)

- Steps 1-3 (mux.dev HLS, seeks inside and outside the buffer): passed. Picture and sound are in
  sync, where they were audibly out of sync before this slice. FFmpeg's "PES packet size
  mismatch" and "Packet corrupt" lines now log as Info.
- Step 6 (progressive MP4 with ranges, `avsync.mp4` flash and beep): in sync, also after seeks;
  Loop restarts once per lap with sound. `python -m http.server` has no range support and cannot
  serve an MP4 whose index is at the end (recorded in ENG-034); a range-capable server works.
- Close during a stalled network load: closes at once.
- Not run yet: steps 4 and 5 (network loss) and step 7 (Audio mode).
