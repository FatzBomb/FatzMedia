# Stage Update

Stage: 013B.1 - Per-item playback sync controls
Date: 2026-07-11

## Status: IMPLEMENTED - manual Player smoke pending

Delivered:

- Added per-item A/V, CDG, and subtitle offsets with 100ms adjustment operations and a reset command.
- Kept audio as the physical master clock; the A/V offset changes video presentation relative to audio.
- Added compact Player-host controls for all three offsets.
- Added focused sync-state tests.

## Verification

- Release build passes with zero warnings/errors.
- `PlaybackSyncStateTests` pass.
- WPF boundary check passes.
