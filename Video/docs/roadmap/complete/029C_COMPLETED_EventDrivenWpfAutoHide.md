# Completed Stage 029C - Event-Driven Surface AutoHide

Prepared: 2026-08-08
Completed: 2026-08-08
Status: Complete

## Objective

Give the Stage 028A lightweight WPF surfaces a deliberate, event-driven visibility
policy driven by the Stage 029A session lifecycle plus `PresentationFramePresented`
as the positive proof of visual output — never file extensions. AutoHide is enabled
by default (standard media-player behavior); hosts opt out with `AutoHide = false`
or the Player host's `--no-auto-hide` startup flag. A surface shows only when Core
has really presented compatible visual content and hides on explicit stop, close,
failure, or non-loop natural end, while pause keeps it shown and looping keeps it
shown. AutoHide governs only the internal native/composited presentation host; it
never changes the outer control's `Visibility`, background, or transparency.

## Scope (exact)

- **WPF adapter policy plus one additive Core capability.** The visibility policy
  lives in the WPF adapter (`MediaSurface` owns it; `BackgroundSurface` and
  `KaraokeSurface` forward/consume it for their inner `MediaSurface`). Core gained
  one additive public capability — `IMediaControlSession.HasVisualContent` — so the
  adapter can arm hidden presentation targets for restart without exposing renderer
  internals. No renderer changes, no `MediaPresentationCoordinator` / pipeline /
  engine changes.
- `SecondaryPresentationWindow` and overlay windows are **not** governed by the
  policy.
- Public `AutoHide` dependency property, default `true` — standard media-player
  behavior. The outer control stays laid out and attached in every mode so a hidden
  presentation host can still receive the frame that proves new visual content
  exists. When `false`, the policy performs no additional internal-host visibility
  writes and current behavior is preserved unchanged.
- Read-only, bindable `IsContentVisible` dependency property reflecting the policy
  state, and a `ContentVisibilityChanged` event raised on the UI thread on every
  visibility transition.
- One small internal pure state helper: `SurfaceAutoHidePolicy`, shared by
  `MediaSurface` and forwarded/consumed by `BackgroundSurface` / `KaraokeSurface`,
  with pure tests.
- A `FatzBomb.MediaControl.Tests` → `FatzBomb.MediaControl.Wpf` project reference
  was added so the pure policy can be tested; the project-reference allow-list in
  `scripts/check-core-boundary.ps1` was updated explicitly and reviewed for this
  (see Boundary).

## API Compatibility Note (pre-1.0)

- `IMediaControlSession` gains one additive member:
  `bool HasVisualContent { get; }` — true when the loaded media's content role
  includes a visual main (`MediaContent.Video`, `MediaContent.Cdg`,
  `MediaContent.Image`, or `MediaContent.Subtitle`) or when a resolved
  composition background is present. It is implemented on
  `MediaControlPlayer` (line 145: content flags OR `_mediaEngine.Background !=
  null`).
- The addition is **source-compatible** for existing consumers: interface member
  additions do not break call sites or implementers who already inherit from
  `MediaControlPlayer`. As with Stages 029A/029B interface expansion, this is an
  explicitly accepted pre-1.0 additive change; hosts that implement
  `IMediaControlSession` directly (rather than deriving from `MediaControlPlayer`)
  must add the member.
- The WPF adapter consumes only this public property plus the existing public
  session events; it gains no renderer/pipeline/internal-Cdg/D3D11 references.

## Pinned Decisions

1. **Signal source** — decisions are driven by the Stage 029A session event surface
   (`MediaLoaded`, `MediaClosed`, `MediaPaused`, `MediaLooped`, `MediaStarted`,
   `MediaStopped`, `MediaEnded`, `MediaError`, `StateChanged`) plus
   `PresentationFramePresented` as the sole positive proof that visual output
   actually reached a surface. **Never** file extensions, and never reject visuals
   merely because `MediaContent` says `Audio` — an audio main with an image/video
   sidecar or archive background presents real frames and must show.
2. **Arm + show proof** — `HasVisualContent` (visual main flags or resolved
   background) arms the pending state so a hidden target can restart presentation;
   a successful load only *arms* pending (show-on-present). The surface becomes
   visible only after a `PresentationFramePresented` while armed. A load that
   never presents (pure audio with no background, or a failed load) never shows.
   A presentation event alone cannot bootstrap a hidden target: a present that
   arrives while `HasVisualContent` is false (stale clear/present from a replaced
   item) is filtered out by the adapter.
3. **Explicit stop vs loaded static image** — distinct states, tracked by event
   sequence, not inferred: a load arms pending/show-on-present, presentation shows,
   an explicit `Stop` (or `Close`, or load failure) hides; a loaded static image
   that presented stays shown until an explicit hide signal. Pause keeps it shown.
4. **Loop semantics** — non-loop natural end (`MediaEnded`, `LoopMedia = false`)
   hides; a looping end (`MediaEnded`, `LoopMedia = true`) stays shown and
   `MediaLooped` confirms the lap; loop never hides.
5. **Replacement is last-write-wins, with a bounded transient hide** — a new
   `MediaLoaded` while content is shown or hidden re-arms `PendingShow`; the
   newest signal always wins. The adapter cannot distinguish an explicit stop
   from the internal stop a replacement performs (both raise `MediaStopped`), so
   a transient hide during a slow replacement is expected and allowed. The
   guarantees are only: the newest `MediaLoaded` always re-arms, the first
   presented frame of the new content shows, and the surface is never stuck
   hidden. No no-flicker guarantee is made (see Pre-MediaLoaded frame latch /
   replacement contract below).
6. **Stale replacement terminal/frame filtering** — a present or terminal signal
   belonging to a replaced session cannot reveal or re-arm content for a new
   audio-only session. The adapter snapshots `HasVisualContent` on
   `MediaLoaded`, clears it on close/error/hide, and ignores queued
   `PresentationFramePresented` dispatches when the visual-capability flag is 0
   or the session generation has moved on, so a stale clear/present from the
   replaced item never reveals a new audio-only session.
7. **AutoHide opt-out is absolute** — with `AutoHide = false`, the policy is not
   consulted and performs no additional presentation-host visibility write; hosts
   keep full manual control exactly as today. AutoHide never writes the outer
   control's `Visibility`, background, or transparency, even when enabled.
8. **`IsContentVisible`** — read-only bindable DP (`DependencyPropertyKey` +
   public read-only wrapper), updated only on the UI thread from policy
   transitions.
9. **`ContentVisibilityChanged`** — raised on the UI thread on transitions only
   (no per-frame spam from the presentation loop).
10. **Fallbacks survive** — `BackgroundSurface`'s solid-color fallback remains
    visible and is never hidden by inner-media AutoHide; `KaraokeSurface`'s canvas
    fallback remains intact. The policy governs only the inner media surface
    visibility, not the fallback layers.
11. **Pure policy core** — `SurfaceAutoHidePolicy` is a small internal,
    deterministic state machine with no WPF dependencies (no `DependencyObject`,
    no `Dispatcher`), so it is unit-testable as pure logic. The adapter does the
    WPF-specific marshaling around it.

## State Table (exact)

`SurfaceAutoHidePolicy` state: `Idle` → `PendingShow` (armed) → `Shown` → `Hidden`.
Transitions below are exact. `AutoHide = false`: policy not consulted, no additional
presentation-host visibility writes, all rows become "untouched".

| # | Signal (Stage 029A surface) | AutoHide on | AutoHide off | Notes |
|---|------------------------------|-------------|--------------|-------|
| 1 | Initial / no media | `Idle` → hidden | untouched | surface starts hidden |
| 2 | `MediaLoaded` (success) | `Idle`/`Shown`/`Hidden` → `PendingShow` | untouched | arms; does **not** show yet |
| 3 | `PresentationFramePresented` in `PendingShow` | → `Shown` (show) | untouched | positive proof of visual output; adapter also requires the visual-capability flag |
| 4 | `PresentationFramePresented` in `Shown` | stays `Shown` | untouched | repeat frames are no-ops |
| 5 | `MediaStarted` | `Hidden` → `PendingShow`; otherwise no change | untouched | replay after explicit stop re-arms; next presented frame shows |
| 6 | `MediaPaused` | stays `Shown` | untouched | pause remains shown |
| 7 | `MediaStopped` (explicit stop) | → `Hidden` | untouched | explicit stop hides |
| 8 | `MediaClosed` | → `Hidden` | untouched | close hides |
| 9 | `MediaError` (load failure or fatal) | → `Hidden` | untouched | failure hides; failed load never presents |
| 10 | `MediaEnded`, `LoopMedia = false` | → `Hidden` | untouched | non-loop natural end hides |
| 11 | `MediaEnded`, `LoopMedia = true` | stays `Shown` | untouched | loop stays/shows |
| 12 | `MediaLooped` | `Hidden` → `PendingShow`; otherwise no change | untouched | loop confirmation can re-arm; next presented frame shows |
| 13 | New `MediaLoaded` while open (replacement) | → `PendingShow` | untouched | last-write-wins; newer load beats prior hidden/shown |
| 14 | Loaded static image | `PendingShow` → `Shown` on first present | untouched | shows on presentation proof, never on load alone |
| 15 | Load never presents (pure audio, no background; failed load) | stays `Hidden` | untouched | no presentation, no show |

Notes on the table:

- **Never consult `MediaContent` / `MediaSourceInfo.Content` / file extensions.** An
  audio-main session with a sidecar/archive background presents frames (row 3) and
  shows; pure audio without visuals never presents and stays hidden (row 15). The
  same rule covers video, image, CDG, and MIDI/KAR mains. `HasVisualContent` is
  consulted only as the arming gate for hidden targets and the stale-frame filter —
  never as a show decision by itself.
- **Loop vs natural end** — decided by `LoopMedia` at `MediaEnded` time plus the
  `MediaLooped` confirmation (029A ordering contract), not by content.
- **Replacement** — any `MediaLoaded` re-arms, so
  load(show) → stop(hide) → load(new) ends `PendingShow` and shows on the first
  new present. A replacement's internal stop hides the surface until the new
  content presents (WPF-only signals cannot tell an explicit stop from a
  replacement stop); a transient hide during a slow replacement is allowed. The
  surface is never stuck hidden: the newest load always re-arms and the first
  presented frame shows.

### Pre-MediaLoaded frame latch/replay (static images)

`PresentationFramePresented` can reach the UI thread **before** the corresponding
`MediaLoaded`: the present fires from Core's native present/render thread and
`MediaLoaded` from the control pipeline, and both are marshaled separately to the
dispatcher. A static image typically presents exactly one frame, so a present
that lands while the policy is still `Idle` (not yet armed) would be a no-op —
and with no later present, the surface would stay hidden forever.

The adapter solves this with a **latched frame proof**: `QueueAutoHideFramePresented`
records "a frame was presented" in an interlocked flag (`_autoHideFramePresented`)
that survives the marshaled update. When the queued `MediaLoaded` transition runs
(or the policy is reset on a session change with an already-open session), it
re-arms `PendingShow` and then, if the latch is still set and `HasVisualContent`
is true, immediately calls `FramePresented()` to **replay** the proof —
transitioning straight to `Shown` without waiting for another present. The latch
is cleared only on session change, on hide transitions (stop/close/error), and on
non-loop natural end, so it cannot resurrect a stale show after an explicit hide.

This is why the pure policy test `FrameBeforeLoadDoesNotShowWithoutAdapterReplay`
pins that a frame before load is a no-op at the policy level: the policy is
exact, and the adapter's latch/replay is the deliberate mechanism that makes
static-image loads (and any first-present-before-load race) show without an extra
present.

## API Surface

### Core (additive, public)

- `IMediaControlSession.HasVisualContent` — `bool`, true when the loaded media has
  a visual main role (Video/Cdg/Image/Subtitle) or a resolved composition
  background. Implemented on `MediaControlPlayer`. Consumed by the adapter as the
  arming gate for hidden targets and the stale-replacement-frame filter.

### WPF adapter only

- `MediaSurface.AutoHide` — public `DependencyProperty`, `typeof(bool)`, default
  `true`; governs the internal native/composited host without
  changing the outer WPF control's `Visibility`, background, or transparency.
- `MediaSurface.IsContentVisible` — public read-only bindable DP (true when the
  policy is `Shown`), updated on the UI thread.
- `MediaSurface.ContentVisibilityChanged` — `EventHandler` raised on the UI thread
  when `IsContentVisible` transitions.
- `BackgroundSurface` / `KaraokeSurface` forward their inner `MediaSurface`
  AutoHide state: same `AutoHide` DP semantics, same `IsContentVisible` /
  `ContentVisibilityChanged` surface. Their own fallback layers (solid color for
  `BackgroundSurface`, canvas for `KaraokeSurface`) are independent of the inner
  policy.

## Delivered Files

- `FatzBomb.MediaControl/Api/IMediaControlSession.cs` — additive
  `bool HasVisualContent { get; }` (see API Compatibility Note).
- `FatzBomb.MediaControl/Api/MediaControlPlayer.cs` — `HasVisualContent`
  implementation: content role includes `Video`/`Cdg`/`Image`/`Subtitle`, or
  `_mediaEngine.Background != null`.
- `FatzBomb.MediaControl.Wpf/SurfaceAutoHidePolicy.cs` (new, internal, pure) —
  the `Idle → PendingShow → Shown → Hidden` state machine; no WPF types;
  mutated only on the UI thread.
- `FatzBomb.MediaControl.Wpf/MediaSurface.AutoHide.cs` (new partial) — `AutoHide`
  DP (default `true`), read-only bindable `IsContentVisible` DP,
  `ContentVisibilityChanged`; marshaled, coalesced `Dispatcher.BeginInvoke`
  transitions; the pre-`MediaLoaded` frame latch/replay; the stale
  replacement-frame filter (session-generation check plus the
  `_autoHideHasVisualContent` gate); `AutoHide = false` performs no policy-owned
  host visibility writes.
- `FatzBomb.MediaControl.Wpf/MediaSurface.cs` — became `public sealed partial
  class`; AutoHide event wiring extended across the existing
  `AttachSessionEvents` / `DetachSessionEvents` pair (`MediaLoaded`,
  `MediaClosed`, `MediaPaused`, `MediaLooped`, `MediaError`, plus the existing
  `MediaStopped`, `MediaEnded`, `PresentationFramePresented` hooks); session
  change re-arms the policy (`OnAutoHideSessionChanged`).
- `FatzBomb.MediaControl.Wpf/BackgroundSurface.cs` — forwarded `AutoHide`,
  `IsContentVisible`, `ContentVisibilityChanged`; solid-color fallback stays
  visible while inner-media AutoHide hides the media layer;
  `PrepareMediaLayerForLoad` hides the media layer during load when AutoHide is
  on.
- `FatzBomb.MediaControl.Wpf/KaraokeSurface.cs` — same forwarding; opaque canvas
  fallback remains intact.
- `FatzBomb.MediaControl.Wpf/FatzBomb.MediaControl.Wpf.csproj` —
  `InternalsVisibleTo("FatzBomb.MediaControl.Tests")` so the pure policy is
  testable.
- `FatzBomb.MediaControl.Tests/FatzBomb.MediaControl.Tests.csproj` — added the
  `FatzBomb.MediaControl.Wpf` project reference (pure policy only; no
  renderer/pipeline/internal-Cdg/D3D11 types referenced).
- `FatzBomb.MediaControl.Tests/SurfaceAutoHidePolicyTests.cs` (new) — pure policy
  tests (see Tests).
- `FatzBomb.MediaControl.Tests/MediaControlPlayerTests.cs` — added
  `HasVisualContentIncludesMainVisualsAndResolvedBackgrounds` (audio-only false;
  video main true; audio with resolved background true).
- `scripts/check-core-boundary.ps1` — reviewed allow-list update for the
  `Tests → Wpf` project reference (and the Player host's Software/Windows
  references that arrived with the Stage 029A `--software` option).
- `FatzBomb.MediaControl.Player/App.xaml.cs` — AutoHide enabled by default, with
  `--no-auto-hide` as the validation-host opt-out.
- `FatzBomb.MediaControl.Player/ViewModels/MainWindowViewModel.cs`,
  `FatzBomb.MediaControl.Player/MainWindow.xaml` — `AutoHide` bound from the
  startup flag and current state surfaced in the host status line.
- `docs/CURRENT_ARCHITECTURE.md`, `docs/VALIDATION.md` — updated architecture and
  validation baseline notes.

## Boundary

- The stage adds no `using` that pulls a Core renderer, pipeline, internal-Cdg, or
  D3D11 namespace into the WPF adapter — the repo's most important invariant. The
  policy consumes only the public `IMediaControlSession` events plus the additive
  public `HasVisualContent` property and `MediaPlayerState`/`LoopMedia` from the
  public session contract.
- The project-reference allow-list in `scripts/check-core-boundary.ps1` was
  updated explicitly and reviewed for this stage's additions (the `Tests → Wpf`
  reference, plus the Player host's Software/Windows references carried over from
  the Stage 029A `--software` option). `check-wpf-boundary.ps1` needed no changes:
  the new public members (`AutoHide`, `IsContentVisible`,
  `ContentVisibilityChanged`) and the additive Core property trip no forbidden
  pattern, and the WPF adapter still references only the public session contract.
- Tests may reference the Wpf project (pure policy only); tests must not reference
  renderer/pipeline/internal-Cdg/D3D11 types.

## Threading

- `PresentationFramePresented` fires from Core's native present/render thread;
  `MediaStopped` / `MediaEnded` fire from background threads (BASS callback /
  presentation loop); `MediaLoaded` / `MediaClosed` / `MediaPaused` / `MediaLooped`
  / `MediaError` fire from the Core control pipeline. **None have UI-thread
  affinity** (029A contract); hosts and this adapter marshal.
- The adapter marshals every signal snapshot to the WPF UI thread with a coalescing
  pending flag (existing `BeginInvoke` + flag pattern in `MediaSurface`) so the
  high-frequency `PresentationFramePresented` cannot flood the dispatcher; only
  actual policy transitions touch DPs.
- A separate latched frame-presented flag records that a frame already arrived,
  so a static-image present that lands before its `MediaLoaded` is replayed when
  the load re-arms the policy (see Pre-MediaLoaded frame latch/replay).
- A session-generation counter plus the `HasVisualContent` snapshot filter stale
  dispatches: a queued present from a replaced session whose visual-capability
  flag is 0 (or whose generation moved on) is dropped, so a stale
  clear/present never reveals a new audio-only session.
- `SurfaceAutoHidePolicy` is pure and mutated only on the UI thread from the
  marshaled snapshots; ordering is arrival-order with last-write-wins (row 13).
- `ContentVisibilityChanged` is always raised on the UI thread.
- Existing event detach discipline is preserved: handlers detach on `Player`
  change and on `Unloaded` (extend the current `AttachSessionEvents` /
  `DetachSessionEvents` pair); no leaks across attach/reattach or presentation-mode
  changes.

## Tests (pure)

`SurfaceAutoHidePolicyTests` covers the delivered policy deterministically, no
media/no timers/no Dispatcher:

- `SurfaceAutoHideDependencyPropertiesDefaultOnAndExposeReadOnlyState` — all
  three surfaces default `AutoHide` to `true`; `IsContentVisible` is read-only.
- `LoadRequiresPresentedFrameBeforeShowing` — load arms `PendingShow`; no show
  without a presented frame; first present → `Shown`; repeat frames no-op.
- `ExplicitStopCloseAndErrorHideShownContent` — stop, close, and error each hide.
- `PauseAndLoopSignalsKeepShownContentVisible` — start/pause/loop-end/looped
  keep `Shown`.
- `PlayAfterExplicitStopRearmsUntilNextPresentedFrame` — explicit stop hides;
  replay re-arms `PendingShow`; the next presented frame shows.
- `NonLoopNaturalEndHidesShownContent` — `MediaEnded(false)` hides.
- `ReplacementLoadRearmsAfterStopOrShownContent` — load→stop→load ends
  `PendingShow` and shows on the first new present; a load on `Shown` re-arms
  (last-write-wins).
- `FrameBeforeLoadDoesNotShowWithoutAdapterReplay` — the pure policy treats a
  frame before load as a no-op; the adapter's latch/replay is what shows a
  static image whose single present landed before `MediaLoaded` (see
  Pre-MediaLoaded frame latch/replay).

Plus one session-level test in `MediaControlPlayerTests`:
`HasVisualContentIncludesMainVisualsAndResolvedBackgrounds` — audio-only is
`false`; a video main is `true`; an audio main with a resolved background is
`true`.

## Acceptance Criteria

- No renderer changes; the additive Core `HasVisualContent` capability supplies
  composition truth without exposing renderer internals. All boundary gates pass.
- `AutoHide` defaults to `true`; when explicitly set to `false`, the policy performs no additional
  presentation-host visibility writes and existing behavior is unchanged. The
  outer WPF control's `Visibility`, background, and transparency are never
  policy-owned.
- `IsContentVisible` read-only DP reflects the policy; `ContentVisibilityChanged`
  fires on the UI thread on transitions only.
- State table implemented exactly; `PresentationFramePresented` is the only show
  proof; no extension/content-flag decisions anywhere; a stale present from a
  replaced item (visual-capability flag 0 or old session generation) is filtered
  and cannot reveal a new audio-only session.
- Explicit stop/close/failure hide; pause stays shown; non-loop end hides; loop
  stays/shows; replay after stop re-arms and shows on its next frame; replacement
  re-arms on the newest load and shows on the first new present (a transient hide
  during a slow replacement is allowed; never stuck hidden).
- Pre-`MediaLoaded` frame latch/replay: a static image whose only present lands
  before `MediaLoaded` still shows (latch survives; proof replayed on re-arm).
- `BackgroundSurface` solid-color fallback stays visible while inner media AutoHide
  is hidden; `KaraokeSurface` canvas fallback intact.
- Secondary window and overlay windows unaffected.
- Pure policy tests cover the full table; full suite stays green.

## Validation

Automated validation passed 2026-08-08:

- Release build: 0 warnings/errors.
- 406 tests: pass.
- `dotnet format --verify-no-changes`: pass.
- Core boundary, WPF boundary, and presentation-parity gates: pass.
- Core and WPF nupkg/snupkg packs: pass.
- WPF specialist review and final diff review: no blockers.

Commands:

```powershell
dotnet build FatzBomb.MediaControl.sln --no-restore
dotnet build -c Release FatzBomb.MediaControl.sln --no-restore
dotnet test FatzBomb.MediaControl.sln --no-restore
dotnet format --verify-no-changes --no-restore
.\scripts\check-core-boundary.ps1
.\scripts\check-wpf-boundary.ps1
.\scripts\check-presentation-parity.ps1
dotnet pack FatzBomb.MediaControl -c Release
```

### Manual smoke — passed (2026-08-08)

Manual Player AutoHide smoke was completed by the user and confirmed:

- **Visual ↔ audio-only** — video/image/sidecar-bearing audio shows on the first
  presented frame; pure audio with no background never shows; transitions between
  the two behave per the state table.
- **Video/CDG replacement** — load A → load B mid-play: newest load re-arms,
  first new presented frame shows, never stuck hidden; transient hide during the
  swap accepted.
- **Default behavior** — AutoHide on by default with no startup flag; `--no-auto-hide`
  opt-out preserves manual behavior.
- **Background/transparency semantics** — inner native/composited host hidden/shown
  only; outer control `Visibility`/background/transparency never touched by
  AutoHide; `BackgroundSurface` solid-color and `KaraokeSurface` canvas fallbacks
  stay visible.
- **Public `IsContentVisible` / `ContentVisibilityChanged`** — read-only DP
  reflects the policy and the event fires on the UI thread on transitions only.

The full manual checklist that closed the stage (Release, plus `--software` for
the composited mode, with `AutoHide = true`):

1. Video: load → surface shows after the first presented frame; explicit stop →
   hides; play → shows; pause → remains shown; non-loop natural end → hides.
2. Loop on: video reaches end → stays shown through laps; `MediaLooped` visible.
3. Audio-only with no background: never shows.
4. Audio + image/video sidecar (or archive) background: shows (Content is Audio but
   visuals present).
5. Loaded static image: shows on first present; explicit stop hides; reload shows
   again.
6. Load failure (missing/corrupt file): never shows.
7. Rapid replacement (load A → load B mid-play): newest load re-arms, first new
   presented frame shows, never stuck hidden; a transient hide during the swap
   is allowed (replacement fires `MediaStopped`, indistinguishable from explicit
   stop).
8. `AutoHide = false` (explicit opt-out via `--no-auto-hide`): manual behavior is
   preserved; host changes to the outer control's `Visibility` are never overridden
   by AutoHide.
9. `BackgroundSurface` with solid color: fallback stays visible while inner media
   AutoHide hides the media layer; `KaraokeSurface` canvas fallback intact.
10. Secondary output and overlay windows behave as before through all of the above.

## Risks

- Host/policy `Visibility` fights — avoided because AutoHide owns only the internal
  native/composited presentation host and never writes the outer control's
  `Visibility`, background, or transparency.
- Dispatcher flooding from `PresentationFramePresented` — coalescing pending flag;
  only transitions touch DPs.
- Cross-thread ordering between the inner presentation host and outer control —
  single owner of internal-host visibility (`MediaSurface`), forwarded policy
  output, and no outer `Visibility` writer.
- Loop vs natural-end ambiguity — decided by `LoopMedia` at `MediaEnded` time plus
  `MediaLooped` (029A ordering), test-pinned.
- Static image never "starts" — show proof is presentation, not `MediaStarted` /
  `Playing` state; pinned in tests.
- First-present-before-load race (static images) — a static image may present its
  only frame before `MediaLoaded` reaches the UI thread; the latched frame proof
  plus replay on re-arm shows it without an extra present (test-pinned).
- Stale present revealing a hidden audio-only session — filtered by the
  session-generation check and the `HasVisualContent` gate (present-only cannot
  bootstrap a target).
- Replacement transient hide — WPF-only signals cannot distinguish explicit stop
  from replacement stop, so a slow replacement may hide briefly until the new
  content's first frame presents. Accepted contract; never stuck hidden.
- Boundary regression — allow-list update is explicit and reviewed; no new
  renderer/pipeline/internal-Cdg/D3D11 references.

## Explicit Non-Goals

- Any renderer, coordinator, pipeline, or engine change; the only Core change is
  the additive public `HasVisualContent` capability.
- Extension-based or content-sniffing decisions (including
  `MediaSourceInfo.Content`).
- Auto-show when `AutoHide = false`.
- Animations, layout changes, or new presentation modes.
- Changing Stage 028A surface creation, sizing, or presentation semantics.
- Governing `SecondaryPresentationWindow` or overlay windows.

## Coordination

- Closed 2026-08-08 with automated validation, user-confirmed manual smoke, and no
  review blockers. Recommended next stage:
  `complete/030A_COMPLETED_VisualTransforms.md` (completed 2026-08-17).
