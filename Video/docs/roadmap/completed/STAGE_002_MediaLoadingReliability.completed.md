# Stage 002 — Media Loading, Reliability, Async — Completed

## Summary

Completed the current pass of media-loading and reliability fixes while preserving the Core/WPF boundary.

## Completed Work

- Standalone image and stopped standalone visuals present through the Core/native path.
- Archive and same-base sidecar/background resolution use a flat candidate pool.
- Explicit background arrays are supported on the concrete player API.
- Sidecar/background frames wait for a visible CDG snapshot before first-frame presentation.
- Video without audio and standalone CDG now route tempo through clock-backed behavior.
- Standalone CDG completion-monitor CTS cleanup completed.
- Core async paths use `.ConfigureAwait(false)` where relevant.
- Directory scanner access failures produce low-noise diagnostics while preserving skip behavior.

## Deferred

- One known AVI sidecar/background loop issue remains research-only unless a fresh repro log proves the failure mode.
- Broad structured logging and host-facing error surface moved to later stages.
- Device-loss handling moved to later stages.

## Completion Note

This stage is history. Use it only when debugging regressions in media loading, sidecar/background behavior, standalone visual behavior, or clock-backed no-audio behavior.
