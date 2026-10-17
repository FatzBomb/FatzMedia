# Engineering Issues

Updated: 2026-10-17

This register tracks verified engineering follow-ups that are not product-feature
stages. Items remain here until fixed, deliberately accepted, or promoted into a
bounded roadmap stage.

## In Progress

### ENG-011 - AV1 hardware decode falls back to software on some files

Reported: 2026-10-17 (owner smoke, FFmpeg 9). `Scream 7.mkv` logged "Hardware video decode
produced no usable startup frame; falling back to software"; other AV1 files decode on
the GPU. The log did not say why, because the decoder's reasons went only to
`Debug.WriteLine`, which Release builds drop.

Done so far:
- Every video open now logs `VideoDecoder: Decoding <codec> <profile> <bit depth> <size>
  (<pixel format>), decoder <name> via <device>` or `in software`.
- The fallback warning now names the stream and the reason: FFmpeg withdrew the GPU format
  after the GPU decoder failed to start (unsupported profile, bit depth, or size), never
  offered it, or the last FFmpeg error text.
- Fixed a latent case on the way: if no hardware device could be created, the native `av1`
  decoder (which cannot decode in software) was opened anyway and produced no video, with
  no fallback. The software decoder (`libdav1d`) is now used instead.

Next: the owner sends the new warning for the failing file. If the reason is an
unsupported stream, the fallback is correct and only the message matters. Otherwise fix
the cause.

## Open

### ENG-012 - "Composition present failed" warnings on audio-only seeks

Reported: 2026-10-17 (owner smoke log). Seeking an audio-only MP3 logs `Composition
present failed reason=current position refreshed` (and `presentation started` on load)
as warnings. Playback is unaffected. Pre-existing; not yet examined. Likely the renderer
backend refusing to present when there is no visual layer, which should be quiet rather
than a warning.

## Completed

### ENG-001 - Reset raced queued and in-flight control operations

Resolved: 2026-08-17

Public control admissions now share a lifecycle gate with `MediaControlPlayer.Reset`.
Reset waits behind already-admitted work, rejects new work while teardown is active,
rebuilds the player, and disposes the old operation queue only after the exclusive
turn completes. Caller-thread nested queue execution now runs inline instead of
deadlocking. Focused regression tests cover reset waiting for an admitted load and
nested synchronous queue execution.

Residual constraint: `Reset` is rejected from inside a serialized media control
operation or its inline event handler; hosts must schedule lifecycle replacement after
the callback returns. Background position-notifier handlers are not part of that
reentrancy guard.

### ENG-002 - Player diagnostics blocked media threads on the dispatcher

Resolved: 2026-08-17

The Player host now marshals diagnostic and error updates with guarded
`Dispatcher.BeginInvoke` calls. Background media callbacks no longer wait for the UI
thread, and queued updates are ignored after the view model is disposed.

### ENG-003 - Natural-end loop restart bypassed control serialization

Resolved: 2026-08-17

Natural-end handling now uses the same operation queue as host transport commands.
The engine callback queues end detection, public `MediaEnded` delivery is ordered
next, and loop completion is queued after delivery. A transport-generation token
cancels stale end work when Play, Pause, Stop, or Seek wins the race. Tests pin both
host cancellation from `MediaEnded` and an end callback arriving during Stop.

### ENG-004 - Pure binary and MIDI parsing coverage gaps

Resolved: 2026-08-17

Soft Karaoke `@T`/`@I` parsing now has a pure marker-text seam with focused title,
artist, credit, labeled-field, year, deduplication, and additional-information tests.
The DLS converter has malformed-input and successful minimal DLS-to-SF2 fixtures.
Those fixtures exposed and fixed the DLS `insh`, `rgnh`, `wlnk`, and `wsmp` field
layouts plus an invalid 64-bit RIFF payload-size write and omitted LIST type bytes in
the generated SF2 size declaration.

### ENG-005 - Presentation coordinator integration coverage

Resolved: 2026-08-17

A recording renderer backend and stub subtitle rasterizer now exercise the real
`MediaPresentationCoordinator` without GPU/native dependencies. Tests cover primary
surface attach/resize/live-resize/detach, retained subtitle refresh, frame-event
forwarding, and successful device-loss cleanup/reinitialization/surface recreation.

### ENG-006 - WPF presentation-mode callback threw through bindings

Resolved: 2026-08-17

`MediaSurface`, `BackgroundSurface`, and `KaraokeSurface` now coerce
`PresentationMode` to the applied value while media is open instead of throwing from
a dependency-property callback. The requested binding/base value remains deferred
across in-place replacement and is re-coerced after explicit close/release. An STA WPF
regression test pins that complete transition without native media dependencies.

### ENG-007 - Software backend contract no-ops needed clarification

Resolved: 2026-08-17

`SoftwareRendererBackend` now documents that presentation stretch is carried by each
Core composition frame and that no retained background resource exists to clear. The
no-op methods are intentional stateless renderer behavior, not missing implementation.

### ENG-009 - Settled video/CDG seek could miss its completion event

Resolved: 2026-08-17

Successful video/CDG seeks now immediately test the current position through
near-exact equality, completing no-op seeks without waiting for a signal. Real moves
still complete from video position or CDG invalidation events through the wider settle
tolerance, so a small asynchronous video seek cannot complete from its stale pre-seek
position. Stop and natural end clear pending completion state. Tests cover settle
success, no-op video/CDG, tolerance rejection, small-delta asynchronous settle,
latest-wins, and terminal clearing.

### ENG-010 - Dispose did not share reset admission serialization

Resolved: 2026-08-17

`MediaControlPlayer.Dispose` now waits behind admitted work and shares the reset
admission gate. Teardown runs in an exclusive queue turn, terminal event deliveries
drain before the queue closes, and the disposed flag is published afterward. Tests
cover an admitted load, concurrent reset followed by disposal of the rebuilt engine,
idempotency, and explicit rejection from an inline control event.

### ENG-008 - Stage and navigation documentation drift

Resolved: 2026-08-17

Stage 030A was archived after its manual smoke passed, active/completed roadmap links
were updated, and this engineering register was added.

## Current Validation Baseline

- Release build: 0 warnings/errors.
- Automated tests: 464 passed.
- Formatting, Core boundary, WPF boundary, and presentation-parity gates: passed.
