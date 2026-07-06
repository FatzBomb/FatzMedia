<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: BACKLOG.

# Stage 004B — Release-Visible Diagnostics


## Status

Backlog. Should follow or pair carefully after Stage 004A, but do not bundle unless explicitly promoted together.

## Goal

Make important diagnostics available in Release builds through a host-routable path.

## Why This Matters

Existing diagnostic content is useful, but Debug-only logging is not enough for venue/runtime failures.

## Intended Direction

- Keep normal playback quiet.
- Route high-value events/failures to a production-visible diagnostic surface.
- Preserve lightweight periodic video summaries unless replaced by structured logging.

## Not Allowed

- No per-frame spam.
- No broad logging framework migration unless narrowly justified.
- No behavior changes to timing, decoder, renderer, or media loading.

## First Safe Step

Inventory current `Debug.WriteLine` / `Trace.WriteLine` diagnostics and classify which should become host-visible.

## Validation

Debug + Release build. Runtime check should confirm diagnostics appear in Release for intentional failure cases and do not flood normal playback.
