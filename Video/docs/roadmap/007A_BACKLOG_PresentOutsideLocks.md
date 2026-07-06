<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: BACKLOG.

# Stage 007A — Present Outside Transport-Visible Locks


## Status

Backlog.

## Goal

Reduce lock contention by ensuring expensive GPU present work is not performed while holding transport-visible/pipeline locks.

## Why This Matters

The VLC comparison identified `Present` under multiple locks as a major contention flaw. With vsync, this can stall pause/stop/seek and CDG coordination.

## Intended Direction

- Map current lock ownership around `FrameReady`, coordinator handling, D3D blit, and swap-chain present.
- Move expensive present work outside inappropriate locks if a safe seam exists.
- Preserve frame lifetime and presentation correctness.

## Not Allowed

- No display-rate compositor rewrite.
- No GPU fence/release redesign in this stage unless explicitly promoted.
- No broad `VideoPipeline` split.

## First Safe Step

Produce a lock/lifetime map for present flow before any code edit.

## Validation

High-risk timing/threading/presentation stage. Use full builds/scripts and targeted smoke: play/pause/stop/seek, CDG, video, sidecar video, secondary output, resize/fullscreen.
