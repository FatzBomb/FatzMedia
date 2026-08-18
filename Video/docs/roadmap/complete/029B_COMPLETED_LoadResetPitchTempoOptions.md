# Active Stage 029B - Per-Load Pitch and Tempo Reset Options

Prepared: 2026-08-08
Status: Implemented — sole active stage per the roadmap manifest. Automated
validation passed 2026-08-08 (Release 0 warnings/errors, 397 tests, format /
Core / WPF / parity gates, Core pack). Manual Player reset smoke is pending;
recommended next is Stage 029C.

## Objective

Karaoke and venue hosts often carry pitch/tempo offsets across media changes, so a
new track can start with stale performance settings from the previous one.
Independent, opt-in reset options let hosts start each load from neutral values
without changing default behavior for everyone. This stage consumes the Stage 029A
event surface (`PitchChanged` / `TempoChanged`) to announce load-triggered resets.

## Scope (exact)

- Immutable configuration booleans `ResetPitchOnMediaLoad` and
  `ResetTempoOnMediaLoad` on `MediaPlayerConfiguration`, both defaulting to `false`
  so existing behavior is unchanged.
- The reset applies after a successful load and after the active-engine refresh,
  and before `MediaLoaded` is raised / before autoplay begins.
- Reset targets are the neutral defaults: pitch `0`, tempo `100`.
- The two options are fully independent; enabling one never affects the other.
- A reset raises `PitchChanged` / `TempoChanged` (Stage 029A surface) carrying
  `MediaChangeReason.LoadReset`, only when the value actually changes, and the reset
  events precede `MediaLoaded`.
- No normalization reset, no other parameter resets.

## Pinned Decisions

1. **Config surface** — add to the immutable `readonly record struct
   MediaPlayerConfiguration` (`FatzBomb.MediaControl/Core/Configuration/`):
   `public bool ResetPitchOnMediaLoad { get; init; }` and
   `public bool ResetTempoOnMediaLoad { get; init; }`, both default `false`.
2. **Reset point** — in the `MediaFile.LoadCore` load sequence, after
   `_mediaEffects?.RefreshActiveEngine()` (the active-engine refresh) and before the
   `MediaLoaded` raise. Autoplay (`PlayOnLoad`) already starts after `MediaLoaded`,
   so it observes the neutral values.
3. **Neutral targets** — pitch `0`, tempo `100`.
4. **Independence** — each option resets only its own parameter.
5. **Reason plumbing** — the shared immutable `MediaValueChangedEventArgs<T>` gains
   an additive `MediaChangeReason Reason` property. New enum `MediaChangeReason`
   with `User` (default) and `LoadReset` values. Existing raises keep the default
   `User`, so current consumers are unaffected.
6. **Event rules** — a reset raises its change event only when the value actually
   differs from neutral, with `Reason = LoadReset`; the reset events fire before
   `MediaLoaded` within the Stage 029A ordering contract.
7. **No other resets** — normalization, volume, loop, and sync controls are never
   touched by this stage.

## API Compatibility Note

- `FatzBomb.MediaControl/Api/MediaValueChangedEventArgs.cs` —
  `MediaValueChangedEventArgs<T>` gains `public MediaChangeReason Reason { get; }`
  (additive property). The existing public two-argument constructor
  `(T previousValue, T currentValue)` is preserved for binary and source
  compatibility — it delegates with `MediaChangeReason.User` — and a new
  three-argument overload `(T previousValue, T currentValue, MediaChangeReason reason)`
  carries the reason.
- New `FatzBomb.MediaControl/Api/MediaChangeReason.cs` —
  `public enum MediaChangeReason { User = 0, LoadReset = 1 }`.
- Consumers of `VolumeChanged` / `PitchChanged` / `TempoChanged` /
  `LoopMediaChanged` compile unchanged (additive); existing raises keep the
  default `User`.
- `IMediaControlSession` is unchanged — no new events, no new interface members.
- `MediaPlayerConfiguration` is a `readonly record struct`; adding the two
  `init` properties is source-compatible (`new MediaPlayerConfiguration(...)` and
  `with` expressions compile unchanged, defaults `false`), but the struct's
  layout grows, which is binary/ABI-relevant pre-1.0. Hosts that persist or
  marshal the struct by value across versions should treat it accordingly.

## Delivered Files

- `FatzBomb.MediaControl/Core/Configuration/MediaPlayerConfiguration.cs` — the two
  `bool` `init` properties `ResetPitchOnMediaLoad` / `ResetTempoOnMediaLoad`
  (default `false`).
- `FatzBomb.MediaControl/Api/MediaValueChangedEventArgs.cs` — additive `Reason`
  property plus the three-argument constructor overload; two-argument
  constructor preserved.
- `FatzBomb.MediaControl/Api/MediaChangeReason.cs` (new) — the enum.
- `FatzBomb.MediaControl/Controllers/MediaEffects.cs` — `ApplyLoadReset(...)`
  plus reason-carrying `SetPitch` / `SetTempo` paths that raise
  `PitchChanged` / `TempoChanged` only when the value actually changes.
- `FatzBomb.MediaControl/Controllers/MediaFile.cs` — `LoadCore` applies the
  enabled reset(s) after `_mediaEffects?.RefreshActiveEngine()` and before the
  `MediaLoaded` raise.
- `FatzBomb.MediaControl/Api/MediaControlPlayer.cs` — routes both configuration
  flags into `MediaFile` at initial construction and in `Reset(...)`.
- `FatzBomb.MediaControl.Player/App.xaml.cs` + `ViewModels/MainWindowViewModel.cs`
  — `--reset-pitch-on-load` / `--reset-tempo-on-load` startup validation flags
  wired into the player configuration and surfaced in the host status line; the
  view model consumes `PitchChanged` / `TempoChanged` on the WPF dispatcher so
  load-triggered resets also update the sliders without writing back into Core.
- `FatzBomb.MediaControl.Tests/` — config defaults and independence,
  reset-on-load ordering and reason values, `Reset` reconfiguration, and no-op
  cases (see Focused Tests).
- `docs/CURRENT_ARCHITECTURE.md`, `docs/VALIDATION.md` — updated architecture and
  validation baseline notes.

## Regression Caught During Review

Loop notification was hardened while exercising the Stage 029A event surface:
`MediaControls` now synthesizes `MediaLooped` only for non-video engines that
wrap internally, while `VideoPipeline` reports a successful wrap through the new
internal `IMediaLoopEventSource` (`PlaybackCoordinator` → `PipelineMediaEngine`
→ `MediaControls`). An ended video that did not loop successfully no longer
produces a spurious `MediaLooped`. This is outside the load-reset scope and is
covered by the existing loop tests.

## Focused Tests

- `ResetPitchOnMediaLoad` / `ResetTempoOnMediaLoad` default to `false`.
- With `ResetPitchOnMediaLoad` enabled, loading raises `PitchChanged` to `0` (with
  `LoadReset`) before `MediaLoaded`.
- With `ResetTempoOnMediaLoad` enabled, loading raises `TempoChanged` to `100` (with
  `LoadReset`) before `MediaLoaded`.
- Independence: enabling one option never raises or changes the other.
- No event when the active value is already neutral (reset only on actual change).
- Reset applies at the load boundary only — never mid-playback or on seek.
- User-set values carry `MediaChangeReason.User` and are unchanged when both options
  are disabled (the default).
- Autoplay observes neutral pitch/tempo when the options are enabled.

## Acceptance Criteria

- Both flags exist on immutable `MediaPlayerConfiguration`, default `false`.
- Enabled resets fire before `MediaLoaded` and before autoplay; `MediaLoaded` /
  autoplay always observe the reset value.
- Reset events use `MediaChangeReason.LoadReset`; user-set events use `User`.
- Reset events fire only on an actual change; no duplicate or spurious events.
- Pitch/tempo behavior during playback and across seeks is unchanged; normalization
  is never touched.
- Existing consumer code and tests stay green; the API additions are additive.

## Validation

Automated validation passed 2026-08-08:

- Release build: 0 warnings/errors.
- 397 tests: pass.
- `dotnet format --verify-no-changes`: pass.
- Core boundary, WPF boundary, and presentation-parity gates: pass.
- Core package creation: pass.

Commands:

```powershell
dotnet build FatzBomb.MediaControl.sln --no-restore
dotnet build -c Release FatzBomb.MediaControl.sln --no-restore
dotnet test FatzBomb.MediaControl.sln --no-restore
dotnet format --verify-no-changes --no-restore
.\scripts\check-core-boundary.ps1
.\scripts\check-wpf-boundary.ps1
.\scripts\check-presentation-parity.ps1
```

Manual Player reset smoke — pending:

- Launch the Player with `--reset-pitch-on-load` / `--reset-tempo-on-load`, load
  a track after setting non-neutral pitch/tempo on the previous track, and verify
  the new track starts at pitch 0 / tempo 100 with `LoadReset` events before
  `MediaLoaded` and both sliders return to their neutral positions; verify the
  defaults preserve user values across loads.

## Risks

- Double raising if both the controller and the reset path raise the same event —
  one raising site per event (the reset goes through the same `MediaEffects`
  raising path as user changes).
- Ordering regression: the reset must not push `MediaLoaded` or autoplay later than
  the Stage 029A contract promises.
- Firing on unchanged values — guard by comparing to the neutral target before
  raising.

## Explicit Non-Goals

- Resetting parameters other than pitch and tempo (no normalization, volume, loop,
  or sync resets).
- Heuristic "stale setting" detection; resets happen only when explicitly opted in.
- Resetting mid-playback; reset applies only at the load boundary.
- New events on `IMediaControlSession` — this stage only adds a reason value to the
  existing shared event payload.

## Coordination

- Consumes the Stage 029A event surface; Stage 029C (event-driven WPF AutoHide) is
  the recommended next stage.
