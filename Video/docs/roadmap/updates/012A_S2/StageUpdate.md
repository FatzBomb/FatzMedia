# Stage Update

Stage: 012A.S2 — Route `VideoAudioEngine` through its own `MediaDemuxer`
Date: 2026-07-09

Changed:
- Refactored `FatzBombMedia.MediaControl/Core/Audio/VideoAudioEngine.cs` to
  delegate all `AVFormatContext` plumbing to its own `MediaDemuxer` instance
  (mirroring S1's extraction from `VideoDecoder`):
  - Removed the `AVFormatContext* _formatContext` field; added
    `MediaDemuxer _demuxer = new()`.
  - `Open(string)`: `avformat_open_input`/`find_stream_info` → `_demuxer.Open`;
    `av_find_best_stream(AUDIO)` → `_demuxer.FindBestStream`;
    `_formatContext->streams[...]` → `_demuxer.GetStream`;
    `_formatContext->duration` → `_demuxer.FormatDurationTicks`.
  - `IsOpenCore`: `_formatContext != null` → `_demuxer.IsOpen`.
  - `DecodeAndPushAudioCore`: `_formatContext == null` guard → `!_demuxer.IsOpen`;
    `av_read_frame(_formatContext, packet)` → `_demuxer.ReadPacket(packet)`.
  - `SeekDecoder`: guard + `av_seek_frame` → `_demuxer.IsOpen` + `_demuxer.Seek`.
  - `CloseCore`: `avformat_close_input(&_formatContext)` → `_demuxer.Close()`.
  - `Dispose`: also disposes `_demuxer`.

Scope kept to S2: still **two separate demuxer instances** (two format contexts,
two cursors) — no shared cursor, no packet fan-out. Per-call `AVPacket`
alloc/free pattern unchanged. Non-audio packets still skipped via `stream_index`
check (no stream-level discard added). No seek/loop/discard semantics change.
No `VideoAudioEngine` signature changes. `VideoDecoder` untouched (S1 already
done). BASS push/tempo topology untouched. Core-only; no WPF adapter change.

Validation:
- `dotnet build -c Release FatzBombMedia.sln`: succeeded, **0 warnings**, 0 errors
  (warnings-as-errors on Core).
- `dotnet test -c Release --no-build`: **183 passed, 0 failed, 0 skipped**.
- `scripts/check-wpf-boundary.ps1`: passed.
- `scripts/check-presentation-parity.ps1`: passed.

Still open:
- **Manual on-device smoke not yet run** (operator responsibility): A/V sync,
  tempo ±20%, seek (preview+settle), gapless loop, CDG-paired, audio-only
  regression, media switch. Audio frame-count / end-position and loop-wrap
  sequences must match baseline. No behavior change is expected — pure plumbing
  extraction — but S2's stop condition requires confirming audio parity before
  promoting S3.

Notes:
- Behavior-preserving by construction: only *where the audio format context
  lives* moved. Decode/push/throttle flow, BASS push stream, gapless-loop wrap
  (`WrapDecoderForLoop` seeks the audio decoder back to 0 without stopping the
  BASS channel), and output-latency correction (006A) are unchanged.
- **Stable landing point:** after S2 the codebase is functionally identical to
  today but both `VideoDecoder` and `VideoAudioEngine` sit on the same
  `MediaDemuxer` *type* (still two instances). 012A.2 may pause here indefinitely
  before the riskier S3 (single shared read cursor with per-stream fan-out).
- Do not promote S3 until the manual smoke above is confirmed clean.