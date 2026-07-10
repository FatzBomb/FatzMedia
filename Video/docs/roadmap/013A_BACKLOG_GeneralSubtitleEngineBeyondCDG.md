<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: BACKLOG.

# Backlog Stage 013A — General Subtitle Engine Beyond CDG

## Status

Backlog. Gate cleared — 012A.S3 (shared demuxer) is complete. Architecture is ready.

## Why This Exists

CDG is the current karaoke priority, but general subtitle support is needed for broader media playback, lyric formats, and non-CDG karaoke workflows.

## Gate cleared (was "Not Active Until")

- Shared demuxer (012A.S3) is complete — per-stream packet fan-out provides the exact input path subtitles need.
- The composition overlay layer (`MediaCompositionLayerKind.Overlay`) already composites transparent bitmaps on top of video via D3D11 blend states.
- `CdgPipeline` provides a working template for lifecycle/seek/timing.

## Intended Direction

Design subtitle support as a Core-owned timed overlay system: read subtitle streams from the shared demuxer, parse supported formats, render to bitmap, and push through the existing overlay composition layer. Start with SRT; evolve to ASS, PGS, etc.

## Architecture (what's already there)

| Component | Status |
|-----------|--------|
| SharedDemuxer per-stream fan-out | Done (012A.S3) |
| Overlay composition layer (blend, viewport, shaders) | Done |
| CdgPipeline — seek/timing/lifecycle pattern | Done (reusable template) |
| `CreateCdgOverlayLayerDescriptor` — overlay descriptor factory | Done |

## New work needed

1. Subtitle stream registration in SharedDemuxer (small)
2. SRT parser — new `Core/Subtitle/` directory
3. Text renderer — DirectWrite or similar, text → bitmap
4. `SubtitlePipeline` — timing, seek, lifecycle (modeled on CdgPipeline)
5. Integration with existing overlay layer descriptor

Estimated: 1–2 weeks for SRT MVP.

## Must Not Break

- CDG overlay correctness
- Core composition ownership
- WPF overlay being UI chrome only
- Sidecar/background layering
- Shared demuxer A/V sync

## First Safe Step

Register a subtitle stream in SharedDemuxer and parse SRT timestamps/entries — validate correct packet fan-out and timestamp mapping without touching composition.
