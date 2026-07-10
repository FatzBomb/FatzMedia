# Stage Update

Stage: 012A.S3 — Shared cursor + unified seek (revised, option (e))
Date: 2026-07-10

## Status: IN PROGRESS — automated validation clean; awaiting manual on-device smoke

Changed:
- Added `FatzBombMedia.MediaControl/Core/Media/SharedDemuxer.cs` (revised):
  - Per-stream packet fan-out queues (`Queue<IntPtr>`).
  - **Per-consumer EOF** (no cross-stream propagation — fix for reverted S3 bug #3).
  - **Single `Seek`** that flushes both queues + clears both EOFs (fix for bug #2).
  - **`PrepareLoopWrap`**: drains remaining audio packets into audio queue, then
    seeks cursor to 0. Flushes video queue, clears video EOF. Does NOT touch
    audio queue/EOF. Called by `TryPreSeekLoopStart` instead of `decoder.Seek(0)`.
    Preserves gapless video AND audio (fix for bug #1).
  - **`ClearAudioEof`**: clears audio EOF only (cursor already at 0 from
    `PrepareLoopWrap`). Called by `VideoAudioEngine.SeekDecoder` in shared mode.
- Modified `VideoDecoder.cs`:
  - Dual-mode: `SharedDemuxer? sharedDemuxer` constructor parameter + `_ownsDemuxer`.
  - `Open`: skips container-open if already open; skips `AVDISCARD_ALL` when shared;
    self-registers video stream.
  - `DecodeNextFrame`: `ReadPacket` → `ReadVideoPacket`.
  - `SeekSharedDemuxer(target)`: moves the shared cursor once using the video
    stream's timestamp calculation.
  - `Seek`: in shared mode, flush+discard only (no second container seek).
  - **New `OnContainerSeeked(target, exact)`**: flushes codec, clears EndOfStream,
    sets discard window. Used by `Seek` in shared mode and by
    `TryPreSeekLoopStart` after `PrepareLoopWrap`.
  - `Close`/`Dispose`: only if `_ownsDemuxer`.
  - Added `VideoStreamIndex` property + `FromConfiguration` overload.
- Modified `VideoAudioEngine.cs`:
  - Dual-mode: `SharedDemuxer? sharedDemuxer` constructor parameter + `_ownsDemuxer`.
  - `Open`: skips container-open if already open; self-registers audio stream.
  - `DecodeAndPushAudioCore`: while-loop → single `ReadAudioPacket` call.
  - `SeekDecoder`: in shared mode, calls `ClearAudioEof` + flushes codec + sets
    discard window (no container seek). Works for both regular seek (container
    already seeked by `VideoPipeline.SeekCore`) and loop wrap (cursor at 0 from
    `PrepareLoopWrap`).
  - `CloseCore`/`Dispose`: only if `_ownsDemuxer`.
  - Added `AudioStreamIndex` property.
- Modified `VideoPipeline.cs`:
  - `Open`: creates `SharedDemuxer`, opens it, passes to both engines.
  - `SeekCore`: calls `VideoDecoder.SeekSharedDemuxer(target)` once before the
    audio engine resets BASS and codecs flush. The shared decoder's `Seek` then
    only applies its video discard window.
  - Commit seek adds a bounded 2.5-second shared-demux preroll only when the
    normal 480 ms warmup still has no video frame, preventing audio from
    resuming with an empty video queue on long-GOP media.
  - `TryPreSeekLoopStart`: calls `_sharedDemuxer.PrepareLoopWrap()` + `decoder.OnContainerSeeked(0, false)`
    instead of `SeamlessLoopRestart.TryPreSeekToStart(decoder)`. Then `TopUpDecode`
    as before. Gapless video preserved.
  - `SwapVideoDecoder`/`TryFallbackToSoftwareVideo`: pass `_sharedDemuxer` to new decoder.
  - `Close`: disposes `_sharedDemuxer` after both engines.
  - Hardware probe unchanged (own SharedDemuxer, single-stream).

Key design — option (e) drain-audio-then-seek for gapless loop:
1. Video EOF → `TryPreSeekLoopStart` calls `PrepareLoopWrap()`.
2. `PrepareLoopWrap` drains remaining audio packets into audio queue, seeks to 0.
3. Video reads position-0 packets → decodes frames behind tail. **Gapless video.**
4. Audio plays buffered near-EOF packets from queue. BASS never stops. **Gapless audio.**
5. Audio queue drains → EOF → `WrapDecoderForLoop` → `SeekDecoder(0)` → `ClearAudioEof`.
6. Audio reads position-0 packets. `CompletedLoopCount` advances.
7. `CommitAudioLoopWrap` fires → drops stale tail frames, presents position-0 frame.

Validation:
- `dotnet build -c Release FatzBombMedia.sln`: succeeded, **0 warnings**, 0 errors.
- `dotnet test -c Release --no-build`: **183 passed, 0 failed, 0 skipped**.
- `scripts/check-wpf-boundary.ps1`: passed.
- `scripts/check-presentation-parity.ps1`: passed.

Still open:
- **Manual on-device smoke not yet run.** Critical tests:
  1. Video-with-audio playback (H.264+AAC, HEVC HW, AV1 SW, AVI).
  2. A/V sync.
  3. Seek (rapid scrubbing — preview + settle). **This was bug #2 in reverted S3.**
  4. **Gapless audio-backed loop.** **This was bug #1 in reverted S3.**
  5. Device-lost recovery (`SwapVideoDecoder` reuses shared demuxer).
  6. Background video (own SharedDemuxer, unaffected).
  7. CDG-paired, media switch, extended soak (10+ min).

Notes:
- Three fixes from reverted S3:
  1. Gapless loop: `PrepareLoopWrap` drains audio before seeking → both stay gapless.
  2. Seek: one `SeekSharedDemuxer` call in `SeekCore` → no second seek overrides
     the shared cursor.
  3. EOF: per-consumer, no cross-stream propagation.
- `TryPreSeekLoopStart` is preserved (not removed) — it calls `PrepareLoopWrap`
  instead of `decoder.Seek(0)`.
- `CommitAudioLoopWrap` is unchanged — video already has position-0 frames from
  `PrepareLoopWrap` + `TopUpDecode`.
