<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: COMPLETED.

# Completed Stage 013A — General Subtitle Engine Beyond CDG

## Status

Completed 2026-07-11. Core-owned text subtitle support is implemented and validated with sidecar and embedded ASS sources.

## Why This Exists

CDG is the current karaoke priority, but general subtitle support is needed for broader media playback, lyric formats, and non-CDG karaoke workflows.

## Gate cleared (was "Not Active Until")

- Shared demuxer is available — per-stream packet fan-out provides the exact input path subtitles need.
- The composition overlay layer (`MediaCompositionLayerKind.Overlay`) already composites transparent bitmaps on top of video via D3D11 blend states.
- `CdgPipeline` provides a working template for lifecycle/seek/timing.

## Intended Direction

Design subtitle support as a Core-owned timed overlay system: read subtitle streams from the shared demuxer, parse supported formats, render to bitmap, and push through the existing overlay composition layer. Start with SRT; evolve to ASS, PGS, etc.

## Architecture (what's already there)

| Component | Status |
|-----------|--------|
| SharedDemuxer per-stream fan-out | Done |
| Overlay composition layer (blend, viewport, shaders) | Done |
| CdgPipeline — seek/timing/lifecycle pattern | Done (reusable template) |
| `CreateCdgOverlayLayerDescriptor` — overlay descriptor factory | Done |

## Delivered

1. Subtitle stream registration in `SharedDemuxer`.
2. SRT, ASS, and SSA sidecar parsing with focused tests.
3. Sidecar discovery and `SubtitlePipeline` lifecycle.
4. Embedded ASS/SSA, SubRip, MOV_TEXT, TEXT, and WebVTT track discovery and selection.
5. Public subtitle track selection and disable API; embedded tracks take precedence over sidecars by default.
6. DirectWrite/Direct2D renderer with public font, size, color, outline, and shadow options.
7. Separate BGRA subtitle composition layer for software and hardware video presentation.

Estimated: 1–2 weeks for SRT MVP.

## Must Not Break

- CDG overlay correctness
- Core composition ownership
- WPF overlay being UI chrome only
- Sidecar/background layering
- Shared demuxer A/V sync

## Validation

- Release build and 207 focused/unit tests pass.
- Player smoke validated SRT/ASS sidecars, embedded ASS playback, track selection, subtitle disable, seek, pause/resume, and media switching.
- The `Scorpion King.mkv` embedded ASS stream was inspected with FFprobe and runtime timing diagnostics. The player selected cues against matching audio/video clocks; apparent intermittent timing was authored in the source subtitle track.

## Deferred

- Bitmap subtitles such as PGS/DVD.
- Full ASS styling, positioning, animation, and karaoke effects.
