# Idea Stage 019A - Video Frame Event Ownership and Reuse

Status: Good idea.

## Why This Is Worth Doing

Clarifying video-frame event ownership would reduce ambiguity for hosts and renderers,
make retention rules enforceable, and create safe opportunities to reduce per-frame
event and wrapper allocations.

## Recommended Direction

- Classify every event consumer as synchronous, asynchronous, retaining, or observing.
- Separate ephemeral callback data from resources that require an explicit lease.
- Design lifetime rules as public API behavior before adding reuse or pooling.
- Provide migration tests for image, background-video, and host event consumers.

## Recommended Immediate Action

Create a consumer/lifetime matrix for `VideoFrameEventArgs` and its native or managed
payloads. Draft one explicit ephemeral-or-leased contract and evaluate its API impact.

## Must Preserve

- Existing frame validity during callbacks.
- Image-frame and retained-background behavior.
- Native frame ownership and release safety.
- Host event exception isolation.

## Explicit Non-Goals

- Silently reusing the existing public event object with different lifetime semantics.
- Exposing mutable shared frame state.
- Pooling native resources without producer coordination.
- Treating fewer allocations as more important than API correctness.
