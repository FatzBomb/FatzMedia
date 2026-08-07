# Idea Stage 031B - Audio Fade and Crossfade Model

Status: Good idea. Recommended after Stage 031A.

## Why This Is Worth Doing

DJ and venue workflows need clean manual fades and crossfades between ordinary local
audio tracks with well-defined gain math and defined event ordering. A deliberate
envelope model - instead of ad-hoc volume ramps - gives hosts predictable, testable
fade behavior and a defined path to optional transition features later.

## Recommended Direction

- Shared fade `Duration` for the outgoing and incoming envelopes.
- Envelope model: `Outgoing` and `Incoming` are collections of normalized
  position/gain points (position `0..1` across the fade, gain `0..1`).
- Implicit defaults: outgoing `[(0,1),(1,0)]` (starts at full, ends muted), incoming
  `[(0,0),(1,1)]` (starts muted, ends full).
- Points are exact: duplicates at the same position use last-write-wins/upsert,
  including overriding the implicit 0/1 defaults. No epsilon-based merging of
  near-positions - the supplied points are authoritative.
- The envelope snapshot is sorted and immutable; reconfiguration produces a new
  snapshot rather than mutating a live one.
- Gain is multiplicative over user volume and normalization: final gain equals
  envelope gain x user volume x normalization, so fades never fight the volume model.
- Incoming playback starts when its envelope leaves a zero plateau, so muted lead time
  does not consume source audio - a leading all-zero segment delays the incoming
  transport start until the envelope becomes nonzero.
- BASS concurrent handles: the outgoing and incoming tracks play on separate handles
  during the crossfade, with defined takeover at the end of the fade.
- Compare chained native ramps (BASS channel attribute ramps) against an optional
  future BASSmix dependency; BASSmix is not added unless the chained-ramp approach
  proves insufficient. Local ordinary audio is the first target.
- Loop precedence and event ordering are defined explicitly: what happens when the
  outgoing track loops during a fade, and which Stage 029A events fire at each fade
  boundary and state change.
- Video true dissolve is deferred; fade-through-black may be a later stage.

## Recommended Immediate Action

Write the envelope math spec (implicit defaults, upsert semantics, sorted immutable
snapshot, multiplicative gain ordering, zero-plateau start rule) and pin it with pure
unit tests before touching any BASS integration.

## Must Preserve

- User volume and normalization remain the multiplicative anchors of final gain.
- Existing pitch/tempo behavior and the Stage 020A signal chain.
- Loop correctness and current behavior when no fade is configured (default).
- Non-blocking control APIs; fades must not block the calling thread.

## Explicit Non-Goals

- Video dissolve or fade-through-black in this stage.
- Forcing a BASSmix dependency; it stays an optional comparison target.
- Crossfading live or streaming inputs; that belongs with Stage 014A.
- Arbitrary multi-track mixing; this is a two-track fade model, not a mixer or DAW.
