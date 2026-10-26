# Completed Stage 014A - Streaming and Network Playback

Prepared: 2026-10-16
Status: Completed 2026-10-18. Slice 1 (capability matrix, URI source, retry and buffering
contracts) and slice 2 (HTTP/HTTPS audio); owner smoke passed 2026-10-17 (`updates/014A_1`,
`updates/014A_2`). Deferred network video moved to `future/039A_IDEA_NetworkVideoPlayback.md`
(now `complete/039A_COMPLETED_NetworkVideoPlayback.md`).

## Objective

Direct HTTP/HTTPS playback with an explicit per-source capability model, bounded retry,
and buffering/metadata events, while keeping the local-file fast path untouched.

## Capability Matrix (pinned)

| Row | Seek | Duration | Live | Buffered | Retry | Stream metadata | Sidecars | Analysis |
|---|---|---|---|---|---|---|---|---|
| LocalFile | yes | yes | no | no | no | no | yes | yes |
| Archive | yes | yes | no | no | no | no | yes | no |
| InMemoryImage | no | no | no | no | no | no | no | no |
| NetworkFile | with range support | yes | no | yes | yes | if ICY announced | no | no |
| NetworkLive | no | no (0) | yes | yes | yes | yes | no | no |

Events per row: local rows raise only the existing 029A events. Network rows add
`BufferingChanged` (hysteresis: enter <= 5 % fill, exit >= 25 %, progress every
10 points while buffering), `StreamMetadataChanged` (ICY `StreamTitle`, Ogg
`TITLE`/`ARTIST`), and `MediaDiagnostic` entries for connect, timeout, retry, and decode
failures. `MediaEnded` means end-of-file for NetworkFile and stream drop for
NetworkLive. Once open, every row is stopped by Stop, Close, or a new Load. A network
open in progress (connect attempts plus retry delays) is not interruptible; commands
queue behind it (see `updates/014A_2`).

## Pinned Decisions

1. **URI source.** `MediaInputSource.FromUri(Uri, MediaNetworkContent hint)`; only
   absolute `http`/`https`. Behavior comes from capabilities, never URL text.
2. **Classification.** `MediaNetworkContent.Auto` classifies by the URI path extension
   through the same `FileTypes` sets as local files, falling back to audio.
3. **Probe -> row.** A known positive content length is a NetworkFile (seekable only with
   range support); anything else is NetworkLive. Before connecting, a network source is
   assumed live (most conservative row).
4. **Retry.** `NetworkRetryPolicy` defaults to one attempt (no retry). Hosts opt in up to
   10 retries with clamped exponential backoff and a per-attempt connect timeout
   (default 15 s). No infinite retry.
5. **Exclusions.** No sidecars, silence trim, normalization pre-analysis, or prepared-next
   analysis for network sources. Explicit backgrounds still apply.
6. **No DRM/auth/service integrations; no adaptive bitrate.**

## Deferred

- **HTTP video.** `VideoPipeline.Open` and the shared demuxer are file-path based
  (`File.Exists` gate, archive temp-file materialization). Network video needs an
  FFmpeg network open path (`avformat_network_init`, interrupt callback for
  cancellation/timeouts) through the large demux/decoder files. It is a separate
  bounded slice that needs native validation; network sources with a video hint (or a
  video extension) currently fail with `UnsupportedFormat` and a diagnostic.

## Explicit Non-Goals

As in the original idea: DRM, service auth, YouTube, infinite retry, playlist/library
ownership, adaptive bitrate.
