<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: BACKLOG.

# Stage 005B — Device-Lost Recovery


## Status

Backlog. Do not start until Stage 005A detection is working.

## Goal

Recover from device-lost/device-reset by rebuilding the necessary native presentation resources.

## Why This Matters

Detection tells the host what happened; recovery keeps the show running.

## Intended Direction

- Tear down/recreate affected D3D11/composition/swap-chain resources.
- Preserve Core ownership of final presentation.
- Keep WPF adapter limited to surface/control lifecycle.

## Not Allowed

- No broad renderer abstraction rewrite.
- No cross-platform backend work.
- No decoder/shared-demuxer rewrite.

## First Safe Step

After detection exists, map resource ownership/lifetime and define a narrow rebuild boundary.

## Validation

High-risk native/renderer stage. Use full build/boundary/parity checks plus targeted renderer/presentation smoke: video, CDG, image, sidecar background, secondary output, resize/fullscreen.
