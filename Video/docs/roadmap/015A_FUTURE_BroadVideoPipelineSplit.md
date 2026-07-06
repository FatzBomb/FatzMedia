<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: FUTURE.

# Future Stage 015A — Broad VideoPipeline Split


## Status

Future. Proposed order: 015A.

## Why This Exists

`VideoPipeline` is large and owns several concerns: open/close, decode loop, presentation loop, seek pump, timing, queue policy, frame selection, and lifecycle. Splitting it may improve maintainability.

## Not Active Until

Do not promote just because the file is large. Promote only after mapping proves stable seams or after earlier production-safety work exposes a need.

## Intended Direction

Split by stable responsibilities, not by arbitrary line count.

## Must Not Break

- Seek preview/settle behavior.
- Frame selection/drop behavior.
- No-audio video clock behavior.
- Hardware/software fallback.
- Public behavior.

## First Safe Step

Create a responsibility map and identify seams with no behavior change before any source move.
