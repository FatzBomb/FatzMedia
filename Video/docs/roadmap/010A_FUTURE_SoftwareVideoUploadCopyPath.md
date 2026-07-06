<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: FUTURE.

# Future Stage 010A — Software Video Upload/Copy Path


## Status

Future. Proposed order: 010A.

## Why This Exists

The software video path carries extra full-frame CPU copy/upload cost compared with more direct mapped texture approaches. Hardware path remains the preferred 4K/high-refresh route.

## Not Active Until

Do not promote unless software decode performance becomes a real product requirement or profiling proves this path is a bottleneck.

## Intended Direction

Explore reducing software-frame copy traffic while preserving current software fallback correctness.

## Must Not Break

- Software decoder fallback.
- CPU frame pool ownership.
- Native coordinator retained-frame behavior.
- Hardware decode path.

## First Safe Step

Profile software playback at representative resolutions and map every copy from sws_scale output to GPU upload.
