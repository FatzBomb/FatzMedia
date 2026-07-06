<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: FUTURE.

# Future Stage 017A — Display-Rate Compositor / Presentation Rewrite


## Status

Future. Proposed order: 017A.

## Why This Exists

The current source-cadence timer path is improved and stable, but true display-locked pacing may require compositor/presentation redesign for best 23.98/24fps smoothness on fixed-refresh displays.

## Not Active Until

Do not promote until current production-safety and lock/present-lifetime stages are stable, and only if display-cadence stutter remains a proven problem.

## Intended Direction

Move from source-cadence timer presentation toward display-rate scheduling/repeat behavior where appropriate.

## Must Not Break

- Current stable 23.98 memory/timing behavior.
- CDG independent update behavior.
- Multi-target fan-out.
- Secondary output.
- Hardware and software video paths.

## First Safe Step

Research/mapping only: define desired cadence model and identify whether waitable swapchain, DWM/vblank timing, frame repeat policy, or compositor loop changes are required.
