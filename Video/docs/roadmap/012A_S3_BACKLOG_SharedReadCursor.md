<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: DONE — implementation complete, validated, cosmetic limitation accepted (see docs/roadmap/updates/012A_S3/StageUpdate.md).

# 012A.S3 — Shared cursor + unified seek (revised)

Parent: `docs/roadmap/012A_FUTURE_SharedDemuxerInputLayer.md`
Plan: `docs/roadmap/012A.2_PLAN_SharedDemuxerImplementation.md`
Design: `docs/roadmap/012A_S3_DESIGN_SharedCursorUnifiedSeek.md`
Mapping: `docs/roadmap/012A_NOTE_SharedDemuxerInputLayerMap.md`
Prior: S1 (DONE), S2 (DONE), original S3 (REVERTED)

## Status

Completed 2026-07-10. See StageUpdate.md for full detail.

## Goal

Collapse to one `SharedDemuxer` with per-stream packet fan-out + unified seek
semantics + drain-audio-then-seek gapless loop. See design note for full
details.

## Key design decisions

1. **Single `Seek`** on `SharedDemuxer` — flushes both queues, clears both
   EOFs. No `SeekVideo`/`SeekAudio` split.
2. **`PrepareLoopWrap()`** — drains remaining audio packets into audio queue,
   then seeks cursor to 0. Called by `TryPreSeekLoopStart` instead of
   `decoder.Seek(0)`. Preserves gapless video AND audio.
3. **`ClearAudioEof()`** — clears audio EOF only (cursor already at 0 from
   `PrepareLoopWrap`). Called by `WrapDecoderForLoop`.
4. **Per-consumer EOF** — no cross-stream propagation.
5. **`OnContainerSeeked`** — engine methods that flush codec + set discard
   window without seeking the container. Called by `SeekCore` after
   `SharedDemuxer.Seek`.
6. **Dual-mode `_ownsDemuxer`** — standalone/probe/background uses own demuxer.

## Must Not Break

- BASS tempo/pitch push path.
- Video-with-audio sync.
- Seek preview/settle behavior.
- CDG/archive/sidecar workflows.
- Hardware decoder fallback (SwapVideoDecoder reuses shared demuxer).
- Gapless audio-backed loop (drain-audio-then-seek preserves both).
- Background video (own SharedDemuxer, unaffected).

## Stop conditions

- Any A/V desync after seek → revert to S2.
- Any audio gap at loop boundary → revert to S2.
- Any SwapVideoDecoder / device-lost regression → revert to S2.
- Any starvation/overflow → revert to S2.
