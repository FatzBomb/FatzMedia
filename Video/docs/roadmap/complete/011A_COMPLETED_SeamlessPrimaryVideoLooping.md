<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: COMPLETED.

# Completed Stage 011A - Seamless Primary Video Looping

## Status

Completed 2026-07-11. Video-only loops now prepare position-zero frames before the
virtual playback clock reaches the loop boundary.

## Delivered

1. Video-only playback now pre-seeks the shared demuxer to zero when the decoder reaches EOF with tail frames still queued.
2. The decoder preserves queued tail frames and refills position-zero frames behind them before the loop boundary.
3. The virtual playback clock commits the prepared loop at the boundary without the reactive full decoder restart.
4. Presentation keeps source cadence while position-zero frames await the virtual-clock wrap, preventing a busy spin.
5. Audio-backed looping keeps video proactive pre-seek while audio wraps its independent decoder without stopping the BASS channel.
6. Follow-up compatibility fix (2026-07-12): restored an independent audio demux cursor after shared audio packet fan-out caused AV1 + E-AC-3 startup offset until first seek.

## Must Not Break

- Audio-backed gapless looping and A/V sync.
- Normal video playback, seeking, and end-of-media behavior.
- Existing sidecar and background loop behavior.
- WPF adapter boundary.

## Verification

- `dotnet build -c Release FatzBombMedia.sln --no-restore` passes with zero warnings/errors.
- 208 tests pass.
- WPF adapter boundary check passes.
- Manual Player smoke with video-only 23.98fps media: pre-seek buffered 21 frames, wrap committed at `0.000s` with five frames queued, and no drops, late drops, or busy-spin ticks were observed.
- Manual AV1 + E-AC-3 startup A/B isolated the offset to shared audio packet fan-out; independent audio starts synchronized.
- Audio-backed 23.98fps loop committed at `0.014s`, continued playback, and retained bounded source cadence without busy-spin ticks.
