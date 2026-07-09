<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: REVERTED — shared cursor breaks independent seek semantics (gapless loop + SmartSeek). Reverted to S2. Requires S4 (unified seek) to be done first or merged with S3. See docs/roadmap/updates/012A_S3/StageUpdate.md.

# 012A.S3 — Shared read cursor with per-stream packet fan-out

Parent: `docs/roadmap/012A_FUTURE_SharedDemuxerInputLayer.md`
Plan: `docs/roadmap/012A.2_PLAN_SharedDemuxerImplementation.md` (sub-stage S3)
Mapping: `docs/roadmap/012A_NOTE_SharedDemuxerInputLayerMap.md`
Prior: S1 (DONE), S2 (DONE)

## Status

**Reverted.** S3 was implemented and passed automated gates, but manual smoke
revealed two critical regressions: gapless loop freeze at boundary, and AV1
seek freeze. Root cause: a single `av_read_frame` cursor couples seek
positions, breaking the independent seek semantics that gapless loop and
SmartSeek depend on. S3 cannot work without S4 (unified seek semantics).

## What was tried

A `SharedDemuxer` wrapping `MediaDemuxer` with per-stream packet queues,
per-stream EOF flags, and `ReadVideoPacket`/`ReadAudioPacket`/`SeekVideo`/
`SeekAudio`. Both engines in `VideoPipeline.Open` shared one instance.

## Why it failed

- `TryPreSeekLoopStart` seeks video to 0 for gapless loop → moves shared
  cursor → disrupts audio's read position near EOF.
- `SeekCore` calls `SeekAudio` then `SeekVideo` → second seek overrides first.
- Per-stream EOF propagation caused premature audio EOF after video EOF.

## What must change before retrying

S3 (shared cursor) and S4 (unified seek semantics) must be done together, or
S4 must come first. The plan's assumption that S3 could be
behavior-preserving without S4 was wrong: sharing the cursor inherently
changes seek behavior.

## Current state

Reverted to S2 (stable landing point). Both engines use `MediaDemuxer`
(two separate instances, independent cursors). All automated gates pass.
