<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: BACKLOG.

# Stage 007B — Hardware Frame Release Safety


## Status

Backlog. Prefer after Stage 007A mapping/work.

## Goal

Review and improve D3D11 hardware frame release safety so FFmpeg/D3D surfaces are not recycled before GPU work is safely complete.

## Why This Matters

Hardware frames are zero-copy and efficient, but release timing without a GPU completion boundary can create driver-dependent visual artifacts.

## Intended Direction

- Map FFmpeg hardware-frame ownership and D3D11 present/blit lifetime.
- Decide whether a fence, deferred release, or retained reference boundary is needed.
- Preserve zero-copy hardware path where possible.

## Not Allowed

- No decoder rewrite.
- No shared demuxer work.
- No broad renderer architecture change.

## First Safe Step

Audit hardware frame ownership from decoder queue through composition/present release.

## Validation

High-risk decoder/presentation lifetime stage. Use full builds/scripts and smoke hardware video playback, seek, stop, media switch, secondary output.
