<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: BACKLOG.

# Stage 005A — Device-Lost Detection


## Status

Backlog.

## Goal

Detect GPU device-lost/device-removed/reset conditions and surface them clearly.

## Why This Matters

A long-running venue app must not silently fail forever after driver reset/TDR/device removal.

## Intended Direction

- Detect device-removed/reset at D3D11 present/composition/swap-chain boundaries.
- Surface the condition through the error/diagnostic path.
- Do not recover/rebuild yet.

## Not Allowed

- No compositor rebuild in this stage.
- No renderer architecture rewrite.
- No broad swap-chain policy change.

## First Safe Step

Map every `Present`, swap-chain, D3D11, and DirectComposition failure boundary and identify where device-removed status can be checked.

## Validation

High-risk renderer/native behavior. Use full build/boundary/parity checks. Manual smoke only for normal playback plus any reproducible simulated failure path.
