<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: COMPLETED.

# Stage 004B — Release-Visible Diagnostics

> **Status: Complete.** Implementation adds an internal `DiagnosticRouter` static event sink and a host-facing `MediaDiagnostic` event on `IMediaControlSession`. 23 engine/pipeline files instrumented with `DiagnosticRouter.Raise(...)` calls at key diagnostic points. See `docs/roadmap/completed/004B_BACKLOG_ReleaseDiagnostics.completed.md` for full summary.

## Original Goal

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
