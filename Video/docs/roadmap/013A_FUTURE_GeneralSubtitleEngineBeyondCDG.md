<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: FUTURE.

# Future Stage 013A — General Subtitle Engine Beyond CDG


## Status

Future. Proposed order: 013A.

## Why This Exists

CDG is the current karaoke priority, but future general subtitle support may be needed for broader media playback, lyric formats, or non-CDG karaoke workflows.

## Not Active Until

Do not promote until shared demux/input direction is clearer, or until a specific subtitle format is required.

## Intended Direction

Design subtitle/SPU support as a Core-owned timed overlay system that does not move media overlay responsibility into WPF.

## Must Not Break

- CDG overlay correctness.
- Core composition ownership.
- WPF overlay being UI chrome only.
- Sidecar/background layering.

## First Safe Step

Define desired subtitle formats and map where timed text/bitmap overlays would enter the composition model.
