# Stage Update

Stage: 012A.S3 — Shared cursor + unified seek (revised, option (e))
Date: 2026-07-10

## Status: COMPLETED — all implementation done, manual smoke clean, cosmetic limitation accepted

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
- Manual Player smoke: seek, audio-backed loop, and A/V sync all clean.
- Final seek-frame continuity fix: commit seek now presents only the first
  post-target frame and preserves its queued successors for playback. Previously
  the selector discarded those successors, causing playback to jump several
  frames beyond the settled seek visual.
- The first target in a seek burst is exact, so a lone timebar click shows the
  same frame that settled playback uses. A second changing target enables nearby
  preview frames for responsive active scrubbing; the final settle remains exact.
- Exact first-target previews use settle-level decode effort. If no frame is
  produced, the seek worker settles immediately instead of retaining the old
  position through the normal pump interval.

Accepted limitation:
- Single-click seek may show a frame slightly displaced from the playback-start
  frame — this is inherent to the two-pass (preview → settle) model and not
  worth destabilising the clean media path to eliminate.
- Active scrubbing (changing targets mid-burst) shows responsive nearby preview
  frames; the final settle remains exact.

Broader regression checks (future, not blocking this stage):
- Device-lost recovery, background video, CDG-paired playback, media switch,
  extended soak.

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
