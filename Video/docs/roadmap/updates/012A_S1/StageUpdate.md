# Stage Update

Stage: 012A.S1 — Introduce `MediaDemuxer` behind `VideoDecoder`
Date: 2026-07-09

Changed:
- Added `FatzBombMedia.MediaControl/Core/Media/MediaDemuxer.cs` — internal
  `unsafe` `IDisposable` that owns a single `AVFormatContext` and exposes the
  container primitives: `Open`, `FindBestStream`, `GetStream`, `StreamCount`,
  `SetStreamDiscard`, `FormatDurationTicks`, `ReadPacket`, `Seek`, `Close`.
  Each primitive mirrors the exact FFmpeg call `VideoDecoder` used inline,
  returning the same result codes.
- Refactored `FatzBombMedia.MediaControl/Core/Video/VideoDecoder.cs` to delegate
  all `AVFormatContext` plumbing to a `MediaDemuxer` instance:
  - Removed the `AVFormatContext* _fmt` field; added `MediaDemuxer _demuxer`.
  - `Open`: `avformat_open_input`/`find_stream_info` → `_demuxer.Open`;
    `av_find_best_stream` → `_demuxer.FindBestStream`; `_fmt->streams[i]` +
    discard loop → `_demuxer.GetStream` / `SetStreamDiscard`; `_fmt->duration`
    → `_demuxer.FormatDurationTicks`.
  - `DecodeNextFrame`: `_fmt == null` guard → `!_demuxer.IsOpen`;
    `av_read_frame` → `_demuxer.ReadPacket`.
  - `Seek`: guard + `av_seek_frame` → `_demuxer.IsOpen` + `_demuxer.Seek`.
  - `Close`: `avformat_close_input(&_fmt)` → `_demuxer.Close()`.
  - `Dispose`: also disposes `_demuxer`.

Scope kept to S1: `VideoAudioEngine` untouched; no shared cursor; no seek/loop
semantics change; no `VideoDecoder` signature changes; non-video `AVDISCARD_ALL`
optimization preserved. Core-only; no WPF adapter change.

Validation:
- `dotnet build` (Debug, Core project): succeeded, 0 warnings, 0 errors.
- `dotnet build -c Release FatzBombMedia.sln`: succeeded, **0 warnings**, 0 errors
  (warnings-as-errors on Core).
- `dotnet test -c Release --no-build`: **183 passed, 0 failed, 0 skipped**.
- `scripts/check-wpf-boundary.ps1`: passed.
- `scripts/check-presentation-parity.ps1`: passed.

Still open:
- **Manual on-device smoke not yet run** (operator responsibility): video frame
  PTS sequence vs an S0 baseline on H.264+AAC / HEVC hardware / AV1 software /
  AVI; plus A/V sync, seek (preview+settle), stop, media switch, gapless loop,
  secondary output, and hardware device-lost recovery. No behavior change is
  expected — this is a pure plumbing extraction — but S1's stop condition
  requires the PTS sequence to match, so confirm before promoting S2.
- S0 baseline capture was not performed automatically; if not already captured,
  do it as part of the manual smoke.

Post-review tweaks (after /review-diff):
- Stage file status changed BACKLOG → "IN PROGRESS — code complete, awaiting
  manual on-device smoke".
- `MediaDemuxer.Dispose()` simplified to `=> Close();` (removed unnecessary
  `GC.SuppressFinalize` — sealed class, no finalizer). Re-verified Release build
  clean (0 warnings).
- Diff review: no must-fix code issues; scope clean; VideoAudioEngine untouched.

Notes:
- Behavior-preserving by construction: only *where the format context lives*
  moved. All decode flow, HW/SW selection, duration derivation, EOF/error
  contract, and the discard optimization are unchanged.
- `MediaDemuxer.Close()` runs inside `VideoDecoder.Close()`, matching the old
  `avformat_close_input` lifetime exactly — so `SwapVideoDecoder` and the
  device-lost throwaway probe (`HardwareDecodeDeviceAvailable`) tear down and
  rebuild identically.
- This is the seam for S2 (route `VideoAudioEngine` through its own
  `MediaDemuxer`) and later S3 (single shared read cursor). Do not promote S2
  until the manual smoke above is confirmed clean.
