<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: COMPLETED.

# Completed Stage 013B - Playback Sync Controls

## Status

Completed 2026-07-11. Added operator-facing, per-item synchronization controls for A/V, CDG, and subtitle timelines.

## Delivered

1. Independent audio/video, CDG, and subtitle offsets with a +/-30 second safety clamp.
2. Public session properties, 100ms step-adjustment methods, and reset operation.
3. Compact Player-host sync controls with A/V, CDG, and subtitle minus/plus buttons plus Reset.
4. Audio remains the physical master clock; A/V offset shifts video presentation relative to audio without interrupting audio transport.
5. CDG and subtitle offsets apply only to their corresponding render/cue-selection timelines.
6. Automatic reset of all offsets whenever the current media item closes or changes.

## Must Not Break

- Audio transport and output latency handling.
- Video seek, looping, and presentation timing.
- CDG rendering and retained overlay updates.
- Subtitle track selection and cue timing.
- WPF adapter boundary.

## Verification

- `dotnet build -c Release FatzBombMedia.sln --no-restore` passes with zero warnings/errors.
- `PlaybackSyncStateTests` pass.
- WPF boundary check passes.
- Manual Player smoke: adjust each control in 100ms steps while playing, verify Reset and per-item reset after loading another media item.
