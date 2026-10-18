# Idea Stage 039A - Network Video Playback

Status: Idea. Deferred from 014A (completed 2026-10-18), which delivered HTTP/HTTPS audio.

## Why This Is Worth Doing

Network sources play as audio only. A network source with a video hint or video extension
fails with `UnsupportedFormat` and a diagnostic. Hosts that stream video (a media server, a
direct link) cannot use the engine for it.

## Recommended Direction

- `VideoPipeline.Open` and the shared demuxer are file-path based: they gate on `File.Exists`
  and materialize archives into temp files. Add an FFmpeg network open path:
  - `avformat_network_init`;
  - an interrupt callback for cancellation and timeouts;
  - the 014A retry policy and buffering contracts.
- Reuse the 014A capability matrix. Network video is seekable only when the server supports
  ranges, and its duration may be unknown.
- Seeking, prepared-next and looping follow the matrix rather than special cases.

## Must Preserve

- Local video playback, seek and loop behavior.
- Network audio (014A) and its retry and metadata events.

## Recommended Immediate Action

Measure startup time, seek latency and stall behavior for an HTTP MP4 and an HLS stream
through FFmpeg alone before changing the pipeline.
