# Active Stage 046A - Adapter and Player Parity

Prepared: 2026-10-26
Status: slice 1 (P1 fixes, shared adapter logic in Core, second-screen parity) implemented,
pending manual smoke (`updates/046A_1`). Slice 2 (KaraokeSurface and BackgroundSurface on every
adapter, WinForms frame sink, overlay and bitmap input) implemented, pending manual smoke
(`updates/046A_2`). Slice 3 (shared player helpers, GPU-first `--renderer=auto`, `--players` in
every player, `Player` facades in the Avalonia and WinForms adapters) implemented, pending manual
smoke (`updates/046A_3`). **The whole stage is implemented, pending manual smoke.**

Owner direction (2026-10-26): "No features solo for one player. All players should share the
same functionality (our wrappers more so)." The wrappers are the adapters
`FatzBomb.MediaControl.Wpf`, `.Avalonia` and `.WinForms`; the players are the three test hosts.
A read-only audit (at 8205d3c) listed the gaps; the owner decided them as below.

## Owner decisions

1. **Shared adapter logic moves into Core** (additive public API): a neutral "has presentation"
   signal (Audio mode), a surface auto-hide tracker (from the WPF `SurfaceAutoHidePolicy` and
   `MediaSurface.AutoHide.cs`), and a neutral presentation-path enum (Avalonia's
   `MediaSurfacePresentationPath` moved to Core, plus `NativeWindow`). Every adapter binds to them.
2. **KaraokeSurface and BackgroundSurface on every adapter.** Their rules move into Core first:
   Background is a real `MediaPlayerMode.Background` (no BASS for a silent background), the
   karaoke acceptance rule (WPF `KaraokeSurface.cs`) becomes Core policy, and the
   `LightweightPlayerController` reload/creation logic becomes a Core helper. Then thin controls
   in WPF, Avalonia and WinForms. Accepted visible change: the WPF `BackgroundSurface` stops
   starting audio.
3. **WinForms catches up**: a frame-sink fallback (software and Vulkan renderers, BGRA blit in
   `OnPaint`) and overlay content (a layered owned window like the WPF `MediaOverlayWindow`).
4. **`--renderer=auto` is GPU first everywhere**: Direct3D11 on Windows (then WARP, then
   software; Avalonia through `FallbackPresentationCoordinatorFactory`), Vulkan elsewhere.
   `--players=1..4` in the WPF and WinForms players too.

Deferred: the WPF GPU-shared path (D3DImage over a shared texture): large, high risk.

## Slice 1 - P1 fixes, shared adapter logic in Core, second-screen parity

Status: implemented, pending manual smoke (`updates/046A_1`).

Acceptance:

1. P1 fixes:
   - An Avalonia projector or secondary `MediaSurface` never forwards `VideoDisplayMode`; only a
     primary surface does (as WinForms). Opening `SecondaryPresentationWindow` keeps the
     session's display mode. Headless test.
   - Audio mode no longer leaves the last frame on the WPF or WinForms surface (Core's
     has-presentation signal; Avalonia switches to it too).
   - WinForms sends the live-resize hint (`SetPresentationSurfaceLiveResize`) from the parent
     form's `ResizeBegin` / `ResizeEnd`, as WPF does from `WM_ENTERSIZEMOVE` / `WM_EXITSIZEMOVE`.
   - The WPF composited primary surface follows DPI changes (`OnDpiChanged` refreshes the layout).
   - WPF adapter notices go through the owner-aware `DiagnosticRouter.Raise(session, ...)`
     (ENG-036) instead of `Debug.WriteLine`.
   - Every adapter raises one Warning per session when `AttachPresentationSurface` returns false.
2. Core (additive public API, threading documented):
   - `IMediaControlSession.HasPresentation` and `HasPresentationChanged` (default members;
     `MediaControlPlayer` implements both).
   - `MediaSurfaceContentTracker`: auto-hide and the Audio-mode signal for any adapter, with the
     029C latch, stale-frame and stale-stop rules.
   - `MediaSurfacePresentationPath` (moved, plus `NativeWindow`) and
     `MediaSurfacePresentationPathText`.
   - All three `MediaSurface`s expose `AutoHide` (default true), `IsContentVisible`,
     `ContentVisibilityChanged`, `IsPresentationSuspended`, `PresentationPath`,
     `PresentationPathReason` and `PresentationPathChanged`.
3. Second screen:
   - WinForms adapter `SecondaryPresentationWindow` with the Avalonia shape
     (`(session, surfaceId)`, `Surface`, `IsFullScreen`, `ShowOnScreen(Screen, fullScreen,
     owner)`, F11, double-click, Escape); the WinForms player uses it instead of its private form.
   - WPF `SecondaryPresentationWindow` hosts a `MediaSurface`; it gains `Surface`,
     `IsFullScreen`, `ShowOnScreen(PresentationScreen, fullScreen, owner)` with monitor
     enumeration through the adapter's native methods (`PresentationScreen`), and the same keys.
   - WPF `MediaSurface` gains `Role`, `SurfaceId`, `EffectiveSurfaceId`, `IsSurfaceAttached` and
     `IsPrimarySurfaceAttached`; `scripts/check-presentation-parity.ps1` checks the new shape and
     the shared adapter surface on purpose.
4. Tests: Core logic in portable tests through doubles; Avalonia headless tests; Windows-only
   WPF and WinForms adapter tests (Windows CI). Release build warning-clean, Portable, Linux,
   Avalonia and Vulkan tests pass, `dotnet format` clean, the four boundary scripts pass.
5. Manual smoke on Windows passes for WPF, Avalonia and WinForms (`updates/046A_1`).

## Slice 2 - KaraokeSurface and BackgroundSurface everywhere, WinForms catch-up

Status: implemented, pending manual smoke (`updates/046A_2`). Decisions taken there: the WinForms
overlay uses the layered window on both paths (a child would need simulated transparency and
per-control hit-test pass-through); a solid background colour stays a fill without a player;
each adapter keeps its own path option instead of the WPF `PresentationMode`; every player gets
a `--lightweight-surfaces` smoke window.

Acceptance:

1. Core owns the rules: a karaoke acceptance policy (the WPF `KaraokeSurface` rule: CDG,
   audio+CDG, archive CDG, MIDI/KAR; video and plain audio rejected after inspection), the
   background control's player in `MediaPlayerMode.Background` (no audio engine, forced loop),
   and a lightweight-player helper (creation, reload coalescing, release) with tests through the
   existing doubles.
2. Thin `KaraokeSurface` and `BackgroundSurface` controls in WPF (reworked onto the Core helper),
   Avalonia and WinForms, with the same properties (`Source`, `AutoPlay`, `PlatformFactory`,
   colours, `AutoHide`/`IsContentVisible`, overlay for the background) and events.
   The WPF `BackgroundSurface` no longer starts audio (accepted visible change).
3. WinForms `MediaSurface` frame-sink fallback: when the session's renderer cannot present into
   an HWND (software, Vulkan), the surface attaches a managed frame sink and blits BGRA frames
   in `OnPaint`, reporting `PresentationPath = FrameSink`.
4. WinForms overlay content: a layered owned window above the surface, like the WPF
   `MediaOverlayWindow`, following moves, resizes and visibility.
5. `WinFormsMediaInputSources.FromBitmap(System.Drawing.Bitmap)` (parity with WPF `ImageSource`
   and Avalonia `Bitmap` conversions; the WPF conversion becomes the static
   `WpfMediaInputSources.FromImageSource`).
6. The WinForms player's `--renderer=software` really uses the software renderer (frame sink).
7. Tests and manual smoke as in slice 1.

## Slice 3 - Shared player helpers and renderer choice

Status: implemented, pending manual smoke (`updates/046A_3`). Decisions taken there: the WPF and
WinForms players share one Windows renderer mapping (`PlayerRendererSelection`, compiled by link);
the Avalonia player's default renderer becomes `auto`; the Avalonia and WinForms adapters get a thin
`Player` facade without a default platform; runtime renderer / player-count switching stays an
Avalonia toolbar feature (owner question).

Acceptance:

1. `Player.Shared` gains the helpers the three players duplicate: the full-screen chrome
   controller (2.5 s timer), the process sampler and fps/stats line, the renderer-name parser
   (factory creation stays in each host), `--surface=` (auto/framesink) and `--second-screen=`
   with one screen-choice rule.
2. `--renderer=auto` picks GPU first on every player: Direct3D11 on Windows (then WARP, then
   software; Avalonia through `FallbackPresentationCoordinatorFactory`), Vulkan elsewhere.
3. `--players=1..4` in the WPF and WinForms players.
4. Small parity items: the Avalonia warning for `--hdr-output`, the WPF seek-bar key step, a
   file drop onto the WinForms video area.
5. The owner question of slice 2: a `Player` convenience class in the Avalonia and WinForms
   adapters if it stays thin (decided in `updates/046A_3`).
6. Tests for the shared helpers; manual smoke on the three players (`updates/046A_3`).
