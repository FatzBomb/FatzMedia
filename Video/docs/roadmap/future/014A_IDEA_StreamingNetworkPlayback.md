# Idea Stage 014A - Streaming and Network Playback

Status: Good idea.

## Why This Is Worth Doing

Network input expands the engine beyond local files to internet radio, remote media,
live feeds, and host-supplied streams. It also gives the input and demux boundaries a
clear capability model for seekable, finite, slow, and reconnectable sources.

## Recommended Direction

- Extend input-source contracts with explicit capabilities rather than URL special cases.
- Separate buffering, reconnect, cancellation, and progress policy from local-file logic.
- Report connection, timeout, decode, and reconnect failures through existing diagnostics.
- Preserve a direct fast path for local files and archives.

## Recommended Immediate Action

Write a source-capability matrix for local files, archives, HTTP files, internet radio,
and live streams. Define seek, duration, buffering, cancellation, retry, and end-of-media
semantics before changing the demux pipeline.

## Must Preserve

- Local file startup and playback performance.
- Archive and ZIP CDG workflows.
- Sidecar detection for file-backed media.
- Existing seek behavior for seekable sources.

## Explicit Non-Goals

- DRM, commercial streaming-service integration, or credential storage.
- Playlist, media-library, or download-manager ownership in Core.
- A full adaptive-bitrate implementation in the first slice.
- Treating every network failure as an automatic infinite retry.
