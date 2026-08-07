# Idea Stage 031A - Host Playlist and Core Prepared-Next Slot

Status: Good idea. Recommended after Stage 030B.

## Why This Is Worth Doing

Venue and karaoke workflows need the next track analyzed and ready before the current
one ends, so transitions are instant. Core should provide that preparation without
taking ownership of the playlist, keeping the engine's no-queue rule intact while
giving hosts a bounded, testable next-item slot.

## Recommended Direction

- Host owns the playlist and queue; Core owns a depth-one prepared-next slot - exactly
  one item may be prepared ahead at any time.
- API surface: `AnalyzeAsync` (analyze the candidate without preparing it),
  `PrepareNextAsync` (analyze and prepare the candidate into the slot), and
  `ClearPreparedNext` (discard the prepared item and free its resources).
- Optional auto transition: when enabled, the prepared item transitions automatically
  when the current media ends; when disabled, the slot simply remains ready.
- Transition-now: a host-triggered switch to the prepared item on demand.
- `PreparedNext` and transition events publish through the Stage 029A session-event
  surface.
- No full queue ownership: no playlist persistence, queue management, or library logic
  in Core; this aligns with the repo's true non-goal of playlist/library ownership
  inside the media engine.
- Analysis feeds through Stage 030B's analysis cache so prepare does not repeat work;
  transitions hook into Stage 031B's fade/crossfade model when configured.

## Recommended Immediate Action

Define the prepared-item state machine (empty, analyzing, ready, transitioning, stale
on media change) and the exact cancellation semantics for `PrepareNextAsync` and
`ClearPreparedNext` before wiring events.

## Must Preserve

- Full host control of playlist and queue contents.
- Current media unaffected while a next item is prepared; preparation is non-blocking.
- Cancellation safety: clearing or replacing the prepared item must not affect current
  playback or leak resources.
- The depth-one bound; the slot never grows into a hidden queue.

## Explicit Non-Goals

- Playlist or queue management inside Core.
- Playlist persistence, ordering rules, or library ownership.
- Multi-item preloading or look-ahead queues.
- Owning host application business logic around playlists.
