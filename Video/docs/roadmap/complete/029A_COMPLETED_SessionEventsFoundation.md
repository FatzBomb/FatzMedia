# Completed Stage 029A - Host-Facing Session Events Foundation

Prepared: 2026-08-07
Completed: 2026-08-08
Status: Complete

## Objective

Give hosts a deliberate, additive session/controller event surface: meaningful,
ordered, throttled notifications instead of low-level callbacks or polling. Only
host-meaningful events were delivered — no event spam, no per-frame events, and no
placeholder events for features that do not exist yet. Later stages (029B, 029C,
031A, 031B, 014A) consume this surface and add their own events using its
conventions.

## Delivered

- Added 12 public events to `IMediaControlSession` / `MediaControlPlayer`:
  lifecycle/state `MediaLoaded`, `MediaClosed`, `MediaPaused`, `MediaLooped`,
  `StateChanged`; position `PositionChanged`, `SeekCompleted`; effect/controller
  `VolumeChanged`, `PitchChanged`, `TempoChanged`, `NormalizeChanged`,
  `LoopMediaChanged`. The existing seven events (`MediaStarted`, `MediaStopped`,
  `MediaEnded`, `PresentationFramePresented`, `MediaError`, `MediaDiagnostic`,
  `AudioDeviceChanged`) behave unchanged.
- Immutable EventArgs payloads, one type per file under `FatzBomb.MediaControl/Api/`,
  following the existing `MediaErrorEventArgs` / `AudioDeviceChangedEventArgs`
  pattern; `MediaSourceInfo` added for load/close payloads.
- Deterministic public ordering contract for load, pause, stop, close, seek,
  natural end, and loop sequences (see below).
- `PositionChanged` throttled/coalesced through a `TimeProvider`-injected 250 ms
  pump (`PlaybackPositionNotifier`) outside the serialized control queue, with
  immediate transition snapshots after load/close/seek/stop/loop and no periodic
  emissions while paused.
- Handler-exception isolation: new events raise through `HostEventDispatcher`,
  which isolates per-handler exceptions and reports them via `MediaDiagnostic`.
- Fixed an overlay visibility bug the manual smoke exposed: the composited overlay
  presenter in `MediaSurface` now sets `Visibility` correctly (overlay content
  previously remained collapsed when assigned in `WpfComposited` mode).
- Added a `--software` validation-host option to the Player host that selects the
  software renderer, software video decoding, and pins `WpfComposited` mode for
  validation runs.

## Event Contracts

- `MediaLoaded` — media source identity, content flags, and duration; raised after
  a successful load, before autoplay begins.
- `MediaClosed` — closed media source identity when available.
- `MediaPaused` — position at pause.
- `MediaLooped` — position after the restart.
- `StateChanged` — previous and current public `MediaPlayerState`.
- `PositionChanged` — position and duration.
- `SeekCompleted` — requested and resulting position.
- `VolumeChanged` / `PitchChanged` / `TempoChanged` — previous and current value.
- `NormalizeChanged` — previous/current enabled flag and current gain.
- `LoopMediaChanged` — previous and current loop flag.

## Ordering Contract (deterministic, public)

Lifecycle/control ordering is a public contract. Immediate `PositionChanged`
snapshots are dispatched outside the serialized control queue but only after the
corresponding queued lifecycle event completes:

- **Load** — `MediaLoaded` → `StateChanged` (when state changed) → immediate
  `PositionChanged`.
- **Autoplay** — starts independently after asynchronous normalization readiness:
  `MediaStarted` → `StateChanged`; no total ordering with the load snapshot.
- **Pause** — `MediaPaused` → `StateChanged`.
- **Stop** — `MediaStopped` → `StateChanged` → immediate `PositionChanged`
  (position 0).
- **Close** — `MediaClosed` → `StateChanged` (when changed) → immediate
  `PositionChanged`.
- **Seek** — `SeekCompleted` → immediate `PositionChanged`.
- **Natural end** — `MediaEnded` → `StateChanged` → immediate `PositionChanged`.
- **Loop after an end signal** — `MediaEnded` first (existing behavior preserved),
  then `MediaLooped` after a successful loop restart, then immediate
  `PositionChanged`. Pipelines already reporting a successful internal loop (video)
  raise `MediaLooped` without a redundant `MediaEnded`.
- **Load replacing open media** — `MediaClosed` for the prior source and
  `MediaLoaded` for the replacement are delivered synchronously before the prior
  source's deferred public `MediaStopped`; hosts should use source identity/generation
  rather than treating that terminal notification as belonging to the replacement.

## Throttling and Threading

- `PositionChanged`: at most one periodic emission per 250 ms (documented constant,
  test-pinned, `TimeProvider`-injectable) during active playback; one immediate
  transition emission; none while paused; last-write-wins for rapid seek sequences.
- Position events never run through or block `MediaControlOperationQueue`; periodic
  snapshots use a dedicated background pump, immediate snapshots use an async
  continuation after lifecycle delivery.
- No UI-thread affinity is guaranteed; hosts marshal before touching UI state.

## Handler-Exception Isolation

New events raise through a shared internal invocation helper that isolates
per-handler exceptions and reports them via `MediaDiagnostic`; a throwing host
handler cannot corrupt playback or block other handlers. Existing events kept their
prior behavior.

## Automated Validation

- Release build: pass, 0 warnings/errors.
- 389 Release tests: pass.
- `dotnet format --verify-no-changes`: pass.
- Core boundary, WPF boundary, and presentation-parity gates: pass.
- Core package creation: pass.

## Manual Validation Confirmed (2026-08-08)

- Player host smoke passed for session events and playback: the new events observed
  across play, pause, stop, seek, natural end, and loop, plus
  volume/pitch/tempo/normalize/loop toggles; no `PositionChanged` while paused and
  no control-queue stalls.
- `WpfComposited` presentation runs at a lower frame rate than `NativeHwndOverlay`;
  this is expected because composited final frames go through managed GPU readback
  rather than the native present path.
- The smoke exposed an overlay visibility bug in `WpfComposited` (assigned overlay
  content remained collapsed); fixed in `FatzBomb.MediaControl.Wpf/MediaSurface.cs`.
- The smoke drove the new `--software` Player-host validation option (software
  renderer + software decoding + pinned `WpfComposited`).
- The software-renderer runtime smoke via `--software` passed on 2026-08-08,
  closing the final manual validation item for this stage.

## Explicit Non-Goals

- Frame payload lifetime work (Stage 019A).
- WPF dispatcher or UI marshaling inside Core.
- Prepared-next/crossfade events (Stages 031A / 031B) and buffering/metadata events
  (Stage 014A).
- Breaking changes to existing event names, payloads, or firing semantics beyond the
  explicitly accepted `IMediaControlSession` interface expansion.
- Per-frame events or event spam.
- Stronger cross-thread ordering than the documented guarantees.
