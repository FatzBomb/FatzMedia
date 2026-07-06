<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: FUTURE.

# Future Stage 019A — VideoFrameEventArgs Reuse / API Redesign


## Status

Future. Proposed order: 019A.

## Why This Exists

`VideoFrameEventArgs` allocation may be reducible, but current public init-only API and image-frame retention make reuse unsafe without redesign.

## Not Active Until

Promote only as an event/API lifecycle redesign, not as a small allocation cleanup.

## Intended Direction

Clarify event payload ownership, retention, and mutability before considering reuse or pooling.

## Must Not Break

- Public API expectations.
- Image frame retention.
- Background/video frame handling.
- Host event safety.

## First Safe Step

Audit all event consumers and classify which event payloads can be ephemeral versus retained.
