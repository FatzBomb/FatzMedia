# Idea Stage 015A - VideoPipeline Responsibility Split

Status: Good idea.

## Why This Is Worth Doing

`VideoPipeline` coordinates open/close, decode, presentation, seek pumping, timing,
queue policy, frame selection, and lifecycle. Separating stable responsibilities would
make those behaviors easier to reason about, test, and change independently.

## Recommended Direction

- Map state ownership, threads, locks, callbacks, and lifecycle transitions first.
- Extract one proven seam at a time behind narrow internal contracts.
- Keep `VideoPipeline` as the orchestrator while responsibilities move incrementally.
- Build and smoke-test after each extraction rather than performing one broad rewrite.

## Recommended Immediate Action

Produce a responsibility and call-flow map for open/close, decode, presentation, and
seek. Select the smallest seam with isolated state and deterministic tests as the first
extraction candidate.

## Must Preserve

- Seek preview and settle behavior.
- Frame selection and drop policy.
- No-audio video clock behavior.
- Hardware/software fallback and device-loss recovery.
- Existing public behavior and diagnostics.

## Explicit Non-Goals

- Splitting by line count or creating many one-method classes.
- Changing timing, queue, seek, or frame-drop policy during extraction.
- Moving platform or renderer implementation details into Core.
- Redesigning the public session API.
