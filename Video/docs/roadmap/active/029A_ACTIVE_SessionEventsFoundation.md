# Active Stage 029A - Host-Facing Session Events Foundation

Prepared: 2026-08-07
Status: Active — bounded and implementation-ready. Sole active stage per the roadmap
manifest; recommended next is Stage 029B.

## Objective

Give hosts a deliberate, additive session/controller event surface: meaningful,
ordered, throttled notifications instead of low-level callbacks or polling. This
stage delivers only host-meaningful events — no event spam, no per-frame events, and
no placeholder events for features that do not exist yet. Later stages (029B, 029C,
031A, 031B, 014A) consume this surface and add their own events using its
conventions.

## Scope (exact)

New public events only. The existing session events are preserved unchanged:

- `MediaStarted`, `MediaStopped`, `MediaEnded`, `PresentationFramePresented`,
  `MediaError`, `MediaDiagnostic`, `AudioDeviceChanged` (all currently declared on
  `IMediaControlSession` / `MediaControlPlayer`).

New events, all additive and public:

- Lifecycle/state: `MediaLoaded`, `MediaClosed`, `MediaPaused`, `MediaLooped`,
  `StateChanged`.
- `PositionChanged` — throttled and coalesced, documented maximum cadence; an
  immediate single emission after load, seek, and stop; no periodic emission while
  paused.
- `SeekCompleted` — carries the resulting position.
- Effect/controller changes: `VolumeChanged`, `PitchChanged`, `TempoChanged`,
  `NormalizeChanged`, `LoopMediaChanged`. The existing `NormalizationGainChanged`
  (MediaEffects controller) and its gain value remain unchanged.

Not in this stage: prepared-next/crossfade events (Stages 031A / 031B) and
buffering/metadata events (Stage 014A). Those later stages add their events using
this stage's conventions.

## Event Contracts

- **Immutable EventArgs** — read-only payloads with no setters, one type per file
  under `FatzBomb.MediaControl/Api/` following the existing `MediaErrorEventArgs` /
  `AudioDeviceChangedEventArgs` pattern.
- **Source and current values** where useful; the event sender remains the session
  or controller, and each payload carries the relevant media/current value:
  - `MediaLoaded` — media source identity, content flags, and duration; raised after
    a successful load, before autoplay begins.
  - `MediaClosed` — closed media source identity when available.
  - `MediaPaused` — position at pause.
  - `MediaLooped` — position after the restart.
  - `StateChanged` — previous and current public `MediaPlayerState`.
  - `PositionChanged` — position and duration.
  - `SeekCompleted` — requested and resulting position.
  - `VolumeChanged` / `PitchChanged` / `TempoChanged` — new value.
  - `NormalizeChanged` — enabled flag and current gain (the existing normalization
    gain stays the source of the gain value).
  - `LoopMediaChanged` — new loop flag.

## Ordering Contract (deterministic, public)

Per-connection ordering is a public contract for these sequences; `PositionChanged`
may coalesce to the documented cadence between transitions:

- **Load / autoplay** — `MediaLoaded` → `StateChanged` → (`MediaStarted` when
  autoplay) → immediate `PositionChanged`.
- **Pause** — `MediaPaused` → `StateChanged`.
- **Stop** — `MediaStopped` → `StateChanged` → immediate `PositionChanged` (position
  0).
- **Seek** — `SeekCompleted` → immediate `PositionChanged`.
- **Natural end** — `MediaEnded` → `StateChanged`.
- **Loop** — `MediaEnded` fires first (existing behavior preserved for
  compatibility), then `MediaLooped` after the loop restart succeeds, then immediate
  `PositionChanged`.

## Throttling and Threading

- `PositionChanged` is coalesced/throttled: at most one emission per 250 ms
  (documented constant, pinned by tests via `TimeProvider` injection) during active
  playback; a single immediate emission after load, seek, and stop; no emissions
  while paused. Rapid seek/stop sequences resolve last-write-wins, with the
  immediate transition emission winning over any in-flight throttled value.
- `PositionChanged` must NOT run through or block the serialized control queue
  (`MediaControlOperationQueue`). It is published from a dedicated background pump
  (the presentation/timing loop) with coalescing; lifecycle/control events remain
  serialized with control operations.
- No UI-thread affinity is guaranteed. Lifecycle and position events may originate
  from background playback/control work, while controller property-change events may
  run on the caller thread. Hosts marshal before touching UI state.

## Handler-Exception Isolation

New events raise through a shared internal invocation helper that isolates
per-handler exceptions and reports them via `MediaDiagnostic`; a throwing host
handler must not corrupt playback or block other handlers. Existing events keep
their current behavior — this stage makes no broad change to them.

## Likely Files

- `FatzBomb.MediaControl/Api/IMediaControlSession.cs` — new session event declarations
  (existing seven stay unchanged). This is additive for consumers but requires any
  third-party interface implementer to add the new members; the package remains
  pre-1.0 and this source-compatibility cost is explicit.
- `FatzBomb.MediaControl/Api/MediaControlPlayer.cs` — session event raising and routing.
- `FatzBomb.MediaControl/Api/*EventArgs.cs` — new immutable payload types, one per
  file per the existing pattern.
- `FatzBomb.MediaControl/Controllers/MediaControls.cs` — lifecycle, state, seek, and
  `LoopMediaChanged` raising sites.
- `FatzBomb.MediaControl/Controllers/MediaEffects.cs` — volume/pitch/tempo/
  normalize raising; keeps `NormalizationGainChanged`.
- `FatzBomb.MediaControl/Pipeline/MediaSession.cs`, `PlaybackCoordinator.cs`,
  `MediaState.cs` — transition, natural-end, and loop signal sources.
- `FatzBomb.MediaControl/Core/Threading/PlaybackPositionNotifier.cs` (new) —
  `TimeProvider`-injected 250 ms position pump and coalescing outside the control queue.
- `FatzBomb.MediaControl.Tests/` — ordering, throttling, immutability, and isolation
  tests.

## Focused Tests

- Ordering sequences for load/autoplay, pause, stop, seek, natural end, and loop
  (including `MediaEnded` → `MediaLooped` → immediate `PositionChanged`).
- Throttling: cadence cap respected; immediate emission after load, seek, and stop;
  no paused spam (`TimeProvider`-injected, deterministic).
- EventArgs immutability and payload values (source/current).
- `StateChanged` transitions and payload.
- Parameter events fire only on an actual change and carry the current value.
- Handler-exception isolation: a throwing handler does not break other handlers or
  playback, and raises a `MediaDiagnostic`.
- `PositionChanged` never executes on the serialized control queue.

## Acceptance Criteria

- All listed events are delivered; the existing seven events behave unchanged.
- `MediaEnded`-on-loop behavior is preserved; `MediaLooped` fires only after a
  successful loop restart.
- `PositionChanged` cadence is documented and test-enforced; it is not raised through
  `MediaControlOperationQueue`.
- EventArgs are immutable; source/current values are present where specified.
- Ordering sequences are documented and covered by tests.
- No prepared-next/crossfade, buffering, or metadata events in this stage.
- Existing consumer code and event semantics remain compatible; third-party
  `IMediaControlSession` implementers must add the newly requested event members.
  Existing tests stay green.

## Validation

```powershell
dotnet build FatzBomb.MediaControl.sln --no-restore
dotnet build -c Release FatzBomb.MediaControl.sln --no-restore
dotnet test FatzBomb.MediaControl.sln --no-restore
dotnet format --verify-no-changes --no-restore
.\scripts\check-core-boundary.ps1
.\scripts\check-wpf-boundary.ps1
.\scripts\check-presentation-parity.ps1
```

Manual: Player host smoke observing the new events across play, pause, stop, seek,
natural end, and loop, plus volume/pitch/tempo/normalize/loop toggles; verify no
`PositionChanged` while paused and no control-queue stalls.

## Risks

- Throttling interaction with rapid seek/stop sequences; immediate transition
  emissions must win over in-flight throttled values (last-write-wins).
- `MediaEnded`-on-loop ordering must not regress; `MediaLooped` is strictly additive.
- Double-raising if both controller and session raise the same event — one raising
  site per event.
- Handler exceptions leaking into playback if isolation is incomplete — isolation is
  scoped to the new events only.
- Position publish cost — coalescing must not allocate per clock tick.

## Explicit Non-Goals

- Frame payload lifetime work (Stage 019A).
- WPF dispatcher or UI marshaling inside Core.
- Prepared-next/crossfade events (Stages 031A / 031B) and buffering/metadata events
  (Stage 014A) in this stage.
- Breaking changes to existing event names, payloads, or firing semantics beyond the
  explicitly accepted `IMediaControlSession` interface expansion.
- Per-frame events or event spam.
- Stronger cross-thread ordering than the documented guarantees.

## Coordination

- Stages 029B and 029C consume this surface now; Stages 031A, 031B, and 014A add
  their own events later using this stage's conventions.
- Stage 019A remains the dedicated design for video-frame event payload lifetimes.
