# Idea Stage 029A - Host-Facing Session Events

Status: Good idea. Recommended next after Stage 024A.

## Why This Is Worth Doing

Hosts today must infer transitions from low-level callbacks or poll session state. A
deliberate session-event surface gives hosts meaningful, ordered, throttled
notifications and gives later stages (load reset, auto-hide, prepared-next, crossfade,
streaming) a single stable place to publish their signals. It also makes the engine's
event contract explicit instead of incidental.

## Recommended Direction

- Deliver meaningful session events, not event spam. The agreed catalog is:
  - Lifecycle: `MediaLoaded`, `Started`, `Paused`, `Stopped`, `Ended`, `Looped`.
  - `StateChanged` for session state transitions.
  - `PositionChanged`, throttled to a documented maximum cadence rather than emitted
    on every clock tick; define behavior when paused (no position spam).
  - `SeekCompleted` carrying the resulting position.
  - Volume, Pitch, Tempo, and Normalization change events.
  - `PreparedNext` and transition events, as hooks for the prepared-next slot
    (Stage 031A) and the fade/crossfade model (Stage 031B).
  - Buffering and metadata events, reserved now and activated when streaming arrives
    (Stage 014A).
- Event args are immutable: read-only payloads that neither Core nor hosts mutate
  after delivery.
- Documented ordering guarantees: per-connection ordering for events that must be
  ordered, which events may coalesce, and which are last-write-wins. Ordering is a
  public contract, not an implementation detail.
- No UI-thread affinity: events fire from background threads and hosts marshal to
  their UI thread, matching the existing `MediaEnded` / `MediaStopped` semantics.
- Additive compatibility only: new events and new payload fields never break existing
  subscribers, and the existing public session API is unchanged.

## Recommended Immediate Action

Inventory the current public events and internal callback sites, then draft the event
catalog with payloads, throttling policy, coalescing rules, and the ordering contract
before implementing any single event.

## Must Preserve

- Existing `MediaEnded` / `MediaStopped` background-thread behavior and host-marshaling
  pattern.
- Existing public API signatures; the session event surface is additive.
- Seek, loop, and pause timing correctness when throttled position reporting is active.
- Event-handler exception isolation so a host handler failure does not corrupt playback.

## Explicit Non-Goals

- Per-frame event delivery; video-frame payload lifetimes remain Stage 019A's scope.
- UI marshaling or dispatcher ownership inside Core.
- Stronger cross-thread ordering than the documented guarantees (fire-and-forget
  delivery on background threads by design).
- Introducing event spam to satisfy hosts that should be polling instead.

## Coordination

- Stage 019A stays the dedicated design for video-frame event payload lifetimes;
  Stage 029A covers session-level lifecycle and state events only.
- Stages 029B, 029C, 031A, 031B, and 014A publish their signals through this surface.
