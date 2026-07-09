# Stage Update

Stage: 012A.S3 — Shared read cursor with per-stream packet fan-out
Date: 2026-07-09

## Status: REVERTED

S3 was implemented, built clean, and passed all automated gates, but manual
on-device smoke revealed two critical regressions caused by the shared read
cursor. S3 has been **reverted to the S2 state** (two separate `MediaDemuxer`
instances). S2 is the stable landing point.

## Bugs found in smoke

### 1. Gapless audio-backed loop freezes at loop boundary

`TryPreSeekLoopStart` pre-seeks the video decoder to 0 while audio is still
playing out buffered audio near EOF. With the shared cursor, this video
pre-seek moves the **single** `av_read_frame` cursor to 0, disrupting audio's
read position. Audio then either:
- sees EOF prematurely (because `ReadVideoPacket` set `_audioEof=true` when
  video hit EOF, and `SeekVideo` didn't clear it), causing an early wrap, or
- reads packets from position 0 instead of near EOF, pushing wrong audio
  content to BASS.

Result: A/V desync at the loop boundary → video freezes.

### 2. AV1 software seek sometimes freezes

`SeekCore` calls `SeekAudio` then `SeekVideo` on the same cursor. The second
seek overrides the first. Audio's next read gets packets from the
video-seeked position, not the audio-seeked position.

## Root cause

A single `av_read_frame` cursor inherently couples seek positions. The
codebase's seek/loop semantics require **independent** seek positions per
consumer:
- Video-flexible SmartSeek (video may show nearby frame, audio hits exact target).
- Gapless loop (video pre-seeks to 0 while audio continues reading near EOF).

This is exactly the §8.2 risk from the mapping note: "Independent seek
semantics." S3 was supposed to only change "how packets are read and buffered,
not how seek/loop behave" — but sharing the cursor inherently changes seek
behavior. **S3 cannot work without S4 (unified seek semantics), which is
design-gated.**

## What was reverted

- Deleted `FatzBombMedia.MediaControl/Core/Media/SharedDemuxer.cs`.
- Reverted `VideoDecoder.cs` to S2 state: `MediaDemuxer _demuxer`, no
  `SharedDemuxer` parameter, `ReadPacket`/`Seek` (not `ReadVideoPacket`/
  `SeekVideo`), unconditional `AVDISCARD_ALL`, always close/dispose.
- Reverted `VideoAudioEngine.cs` to S2 state: `MediaDemuxer _demuxer`, no
  `SharedDemuxer` constructor, `ReadPacket` while-loop, `Seek` (not
  `SeekAudio`), always close/dispose.
- Reverted `VideoPipeline.cs` to S2 state: no `_sharedDemuxer` field,
  separate demuxer instances per engine.

## Validation after revert

- `dotnet build -c Release FatzBombMedia.sln`: succeeded, **0 warnings**, 0 errors.
- `dotnet test -c Release --no-build`: **183 passed, 0 failed, 0 skipped**.
- `scripts/check-wpf-boundary.ps1`: passed.
- `scripts/check-presentation-parity.ps1`: passed.

## Still open

- **Manual on-device smoke** to confirm the revert restored S2 behavior
  (gapless loop, seek, A/V sync). Expected to pass since S2 was already
  smoke-tested clean.

## Notes

- S2 remains the stable landing point: both engines sit on the same
  `MediaDemuxer` *type* (still two instances), with independent cursors.
- S3 (shared cursor) requires S4 (unified seek semantics) to be done first,
  or S3+S4 must be done together. The plan's sequencing was wrong: S3 can't
  be behavior-preserving on its own because sharing the cursor changes seek
  behavior.
- The `012A.2_PLAN_SharedDemuxerImplementation.md` should be updated to
  reflect that S3+S4 must be merged or re-sequenced before another attempt.
