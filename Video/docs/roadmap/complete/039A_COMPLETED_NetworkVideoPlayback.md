# Completed Stage 039A - Network Video Playback

Prepared: 2026-10-24 (idea deferred from 014A, completed 2026-10-18, which delivered HTTP/HTTPS audio).
Completed: 2026-10-26. Follow-ups are in ENG-034.
Status: slice 1 (HTTP/HTTPS progressive video and HLS, one slice) owner smoke passed 2026-10-25
(`updates/039A_1`). Slice 2 (network read-ahead) owner smoke passed 2026-10-26
(`updates/039A_2`).

## Why This Is Worth Doing

Network sources played as audio only. A network source with a video hint or video extension
failed with `UnsupportedFormat` and a diagnostic. Hosts that stream video (a media server, a
direct link, an HLS playlist) could not use the engine for it.

## Scope

- HTTP/HTTPS progressive video (MP4, MKV, any FFmpeg container; byte ranges for seeking) and HLS
  (`.m3u8`, VOD and live) through FFmpeg's network open path.
- The 014A contracts: `MediaInputSource.FromUri`, `MediaNetworkContent`, `NetworkRetryPolicy`,
  `BufferingChanged`, the capability matrix (`MediaSourceCapabilities`).
- Player modes (043A): Full, Background (picture only), Audio (soundtrack only).
- Hosts: the WPF validation host already loads URLs (`--url=`, the URL box); the Avalonia test
  player gains a URL box and URL startup argument.

Non-goals (as 014A): DRM, service auth, YouTube, adaptive bitrate selection (FFmpeg's HLS demuxer
picks the variant), playlist/library ownership, DASH, RTSP/RTMP/SRT.

## Bounded Acceptance Criteria

1. **Open path.** A network video opens through FFmpeg's network protocols:
   `avformat_network_init` once per process; an interrupt callback on every network format
   context; the open bounded by `NetworkRetryPolicy.ConnectTimeout`; every read bounded by the
   same timeout (`rw_timeout`).
2. **Cancellation.** `Close` and `Dispose` issued while a network video connects return at once
   (well under a second), cancelling the attempt and any retry delay. `Close`/`Dispose` of an open
   network video abort a blocked read instead of waiting for its timeout.
3. **Retry.** The open honours the host's policy: `MaxAttempts` attempts with the policy's
   delays and the 014A diagnostics (`Network retry n/m`, `Network stream unavailable after N
   attempt(s)`). Content that connects but has no playable stream is not retried. Mid-stream
   drops use FFmpeg's HTTP reconnect, enabled only when the host opted into retries, bounded by
   `MaxRetries` and `MaxDelay`; each reconnect raises a `Network retry` diagnostic. A stream that
   cannot recover reports `Network read failed` once and ends (`MediaEnded`).
4. **Buffering.** A read blocked for 0.5 s or more while the decoded queue is empty raises
   `BufferingChanged(true)`; the queue refilled raises `BufferingChanged(false)`.
5. **Capabilities.** Progressive: the 014A rule (known length = NetworkFile, seekable only with
   range support; no length = NetworkLive). HLS: VOD (complete playlist, FFmpeg reports a
   duration) = seekable NetworkFile; live = NetworkLive, not seekable, duration 0. Duration is
   known only when FFmpeg reports one. Seeking, looping and prepared-next follow the row, not the
   URL.
6. **Audio.** The soundtrack of network video goes through the same `VideoAudioEngine` path as
   local video (its own connection, no normalization pre-analysis). Without BASS (ENG-033) Full
   mode plays the picture silently. (Slice 2: the audio shares the picture's read-ahead; one
   connection.)
7. **Modes.** Background accepts network video (picture only, looping where seekable) and still
   rejects network audio. Audio mode plays a network video's soundtrack (BASS required; without
   BASS the load is refused with `NativeLibraryMissing`, as for any soundtrack).
8. **Local and 014A behaviour unchanged.** Local video playback, seek and loop; network audio and
   its retry, buffering and metadata events.
9. **Verification.** Pure logic unit-tested (classification, rows, looping, FFmpeg options,
   retry, buffering tracker, mode policy); engine routing tested with test doubles; opt-in Linux
   native tests with real FFmpeg 9 and an in-process HTTP server (progressive frames and seek, HLS
   VOD frames and seek, HLS live row, Close during connect, mid-stream drop with retry and
   buffering, drop without retry ends playback, Background loop, Audio mode without BASS);
   startup and seek latency measured; Avalonia test player screenshots under Xvfb.

## Slices

- **Slice 1 (`updates/039A_1`)** - everything above. HTTP progressive and HLS share the FFmpeg
  path, so they were delivered together rather than as two slices.
- **Slice 2 (`updates/039A_2`)** - network read-ahead (owner's scope: forward read-ahead only; no
  back-buffer, no whole-file cache; a seek outside the buffered range rebuffers from the new
  position). Acceptance criteria:
  1. **Read-ahead.** A background reader demuxes packets of network video ahead of playback into
     a bounded queue: by duration (default 20 s) and bytes (default 64 MB), whichever comes
     first, set by `MediaPlayerConfiguration.NetworkReadAhead` (`NetworkReadAheadPolicy`). Local
     files are unchanged. `BufferingChanged` comes from the buffer: buffering starts when it runs
     dry while playing and ends at a resume threshold (default 2 s).
  2. **One read for picture and sound.** Network video's audio is fed from the same read-ahead
     packets; no second connection. The `_ioGate`/`_sync` order and the ENG-035 rule (getters
     never wait on network I/O) hold; the Audio-mode soundtrack keeps working (its own cursor,
     with its own read-ahead).
  3. **Seek inside the buffer.** A forward seek to a buffered point drops packets up to the
     keyframe at or before the target and decodes on, with no network request. Any other seek
     empties the buffer and seeks as before.
  4. **One network seek per seek.** The preview and settle of a lone network seek share one
     network seek.
  5. **Transport.** Pause and Stop do not wait for a stalled read; Close and Dispose abort the
     reader with the ENG-035 interrupt.
  6. **Verification.** Pure parts unit-tested (accounting, seek-in-range decision, hysteresis);
     opt-in Linux native tests: a 2 s stall with 2+ s buffered (no buffering, no frame gap), the
     caps, a seek inside the buffer (no request, latency), a seek outside (refill), one
     connection with an audio stream, Pause during a stall, Close during a fill; seek latency and
     memory reported.

## Recommended Direction (from the idea, kept for reference)

- `VideoPipeline.Open` and the shared demuxer were file-path based: they gate on `File.Exists`
  and materialize archives into temp files. Add an FFmpeg network open path.
- Reuse the 014A capability matrix. Network video is seekable only when the server supports
  ranges, and its duration may be unknown.
- Measure startup time, seek latency and stall behaviour for an HTTP MP4 and an HLS stream
  through FFmpeg alone before changing the pipeline (done; see `updates/039A_1`).
