# Idea Stage 014A - Streaming and Network Playback

Status: Good idea. Recommended after Stage 031B.

## Why This Is Worth Doing

Network input expands the engine beyond local files to internet radio, remote media,
live feeds, and host-supplied streams. It also gives the input and demux boundaries a
clear capability model for seekable, finite, slow, and reconnectable sources. The
session-events foundation (Stage 029A) provides the buffering and metadata event
surface this stage activates.

## Recommended Direction

- Direct HTTP/HTTPS audio and video first: ordinary progressive HTTP files and
  streams, not adaptive-bitrate delivery in the first slice.
- Source capabilities are explicit, not URL special cases. The agreed capability
  matrix: seekable, duration known, live/unknown-duration, buffering, retry, and
  cancellation semantics, with each capability reported per source.
- URI-based source type: hosts pass a URI and Core queries its capabilities; no
  string-prefix sniffing to infer behavior.
- Buffering and metadata events publish through the Stage 029A session-event surface
  and are reserved by that stage until streaming arrives.
- Separate buffering, retry, cancellation, and progress policy from local-file logic.
- Report connection, timeout, decode, and reconnect failures through existing
  diagnostics.
- Preserve a direct fast path for local files and archives.
- No DRM, YouTube, or commercial-service auth in the first slice, and no automatic
  infinite retry; retry policy is bounded and explicit.

## Recommended Immediate Action

Write the source-capability matrix for local files, archives, HTTP audio files, HTTP
video files, and live streams, covering seek, duration, buffering, cancellation,
retry, and end-of-media semantics - and map each matrix cell to the event it raises on
the Stage 029A surface - before changing the demux pipeline.

## Must Preserve

- Local file startup and playback performance.
- Archive and ZIP CDG workflows.
- Sidecar detection for file-backed media.
- Existing seek behavior for seekable sources.

## Explicit Non-Goals

- DRM, commercial streaming-service integration, credential storage, or service auth.
- YouTube or equivalent platform-specific playback.
- Automatic infinite retry on network failure.
- Playlist, media-library, or download-manager ownership in Core.
- A full adaptive-bitrate implementation in the first slice.
- Treating every network failure as a transport-level emergency without defined retry
  and cancellation semantics.
