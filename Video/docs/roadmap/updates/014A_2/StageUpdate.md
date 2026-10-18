# Stage Update

Stage: 014A.2 - HTTP/HTTPS audio through BASS
Date: 2026-10-16

## Status: IMPLEMENTED - owner smoke passed 2026-10-17 (stage completed 2026-10-18)

Delivered:

- `AudioStreamManager.OpenFromUrl` (`BASS_StreamCreateURL` -> BASS_FX tempo chain, no
  second decode connection), `ProbeNetworkSource` (finite only with `Content-Length` or
  a positive channel length - BASS reports the download-buffer size as `FILEPOS_END`
  for block-streamed sources; ranges from `Accept-Ranges: bytes`; ICY from headers),
  `SampleNetworkBuffer`, ICY/Ogg tag readers.
- `AudioEngine` implements internal `INetworkAudioSource`: bounded retry loop from
  `MediaPlayerConfiguration.NetworkRetry` (default single attempt), per-open BASS
  connect/read timeouts, unity normalization (no pre-analysis), fade stage, end sync,
  250 ms buffering monitor (`BufferingStateTracker`, exit threshold = BASS
  `NetPreBuffer`), and `BASS_SYNC_META` / `BASS_SYNC_OGG_CHANGE` metadata syncs.
- `AudioPipeline.OpenUrl`, `PlaybackCoordinator.OpenNetworkAudio`, and
  `PipelineMediaEngine` routing for `NetworkUri` sources (hint or URI-path extension;
  video rejected with `UnsupportedFormat`), `IMediaSourceCapabilityProvider` for every
  source row, and seek gating for non-seekable network rows only.
- `MediaSourceInfo.Capabilities` now carries the probed row; player exposes
  `CurrentSourceCapabilities`, `BufferingChanged`, `StreamMetadataChanged`.
- Player host: `--url=<http(s)://...>`, `--net-retries=<n>`; logs under `Network`.
- 16 new cases (`NetworkPlaybackTests.cs`) using a real `PipelineMediaEngine` with
  fakes; 014A total 65.

## Decisions made without the owner

- Network retries block the control queue for their bounded duration (load is already
  a queued, blocking operation); they cannot be interrupted mid-connect. Default is no
  retry, so default behavior is a single bounded connect.
- BASS network timeouts are process-wide; each open applies its player's policy.
- Network video deferred (see stage file).
- Local sources keep their existing seek behavior; only buffered (network) rows are
  gated.

## Verification

- Linux: full solution Release 0 warnings/errors; format clean; portable 595/596
  (known Linux sidecar case); 3 repeated runs.

## Manual smoke (Windows, native DLLs, internet access)

1. Internet radio (ICY, e.g. an Icecast MP3 station):
   `dotnet run --project FatzBomb.MediaControl.Player -- --url=<station-url>`.
   Expect `Network stream connected: NetworkLive`, audio plays, Duration 0, seek bar
   rejects seeks, `Stream metadata: artist=... title=...` appears and updates on track
   change.
2. Progressive HTTP MP3 file with known length: `NetworkFile`, duration shown, seek works
   if the server sends `Accept-Ranges: bytes` (otherwise seek is rejected), natural end
   raises `MediaEnded`.
3. Pull the network cable (or throttle) during radio playback: `Buffering=True` then
   `Buffering=False` after reconnect/buffer refill, or `MediaEnded` if the stream drops.
4. Unreachable URL with `--net-retries=2`: two `Network retry` warnings with 1 s / 2 s
   delays, then `Network stream unavailable after 3 attempt(s)`, no hang beyond the
   bounded time.
5. `--url=https://host/clip.mp4`: `UnsupportedFormat` error, no crash.
6. Local file playback, seeking, and archives unchanged.

## Review follow-up 2 (same day)

- Network sources never loop: with `LoopMedia` on, a network file or live stream that ends
  stops and raises `MediaEnded` (previously the player reported Playing with no audio).
- The first `StreamMetadataChanged` of a new stream is delivered after its `MediaLoaded`.
- Extra manual smoke: turn Loop on, play a short progressive HTTP MP3 to its end: playback
  stops cleanly (no endless "Playing" state).
