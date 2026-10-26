# Stage Update

Stage: 046A.1 - Adapter parity: P1 fixes, shared adapter logic in Core, second screen
Date: 2026-10-26

## Status: IMPLEMENTED - pending manual smoke

Owner direction (2026-10-26): "No features solo for one player. All players should share the
same functionality (our wrappers more so)." See `active/046A_ACTIVE_AdapterParity.md` for the
owner decisions and the acceptance criteria of all three slices.

Delivered:

- **Core** (additive public API):
  - `IMediaControlSession.HasPresentation` (default member: `false` in Audio mode, otherwise
    `true`) and `HasPresentationChanged` (default member never raises).
    `MediaControlPlayer` implements both; the change is raised on the control queue after the
    `ModeChanged` of a switch into or out of Audio mode (Full <-> Background raises nothing).
  - `MediaSurfaceContentTracker`: the surface auto-hide rules of Stage 029C, moved from the WPF
    adapter (`SurfaceAutoHidePolicy` is now internal to Core), plus the has-presentation signal.
    Properties `Session`, `AutoHide` (default true), `HasPresentation`, `IsContentVisible`,
    `ShowsContent`; event `Changed`.
  - `MediaSurfacePresentationPath` moved from `FatzBomb.MediaControl.Avalonia` to
    `FatzBomb.MediaControl`, with a new `NativeWindow` value; `MediaSurfacePresentationPathText`
    formats the path line and the attach-refused warning that every adapter raises.
- **WPF adapter**:
  - `MediaSurface`: `Role`, `SurfaceId`, `EffectiveSurfaceId`, `IsSurfaceAttached`,
    `IsPrimarySurfaceAttached`; `VideoDisplayMode` is forwarded only by a primary surface.
  - `MediaSurface`: auto-hide on the Core tracker (same `AutoHide`, `IsContentVisible`,
    `ContentVisibilityChanged`), `IsPresentationSuspended`, and Audio mode hides the host (a
    composited host also drops its frame).
  - `MediaSurface`: `PresentationPath` (`NativeWindow` or `FrameSink`), `PresentationPathReason`,
    `PresentationPathChanged`; `OnDpiChanged` refreshes the layout (composited DPI fix).
  - Notices go through `DiagnosticRouter.Raise(session, ...)` with categories
    `Wpf.MediaSurface`, `Wpf.KaraokeSurface` and `Wpf.BackgroundSurface`: the deferred
    `PresentationMode` (Info, only when a different mode was asked for), a failed source reload
    (Warning), a missing `PlatformFactory` (Warning) and a failed player creation (Error). The
    last two have no player yet, so they are process-wide.
  - `SecondaryPresentationWindow` hosts a `MediaSurface` in its role and the opener's
    presentation mode. It gains `Surface`, `DefaultSize`, `IsFullScreen`,
    `ShowOnScreen(PresentationScreen?, fullScreen, owner)`, F11 / double-click / Escape. The
    constructor and `SurfaceId` are unchanged. A window opened by `MediaSurface.ShowSecondaryOutput`
    takes the opener's `AutoHide`.
  - `PresentationScreen` (new): monitor enumeration through the adapter's `NativeMethods`
    (`EnumDisplayMonitors`, `GetMonitorInfo`, `GetDpiForMonitor`), bounds in device pixels.
- **Avalonia adapter**:
  - `MediaSurface` forwards `VideoDisplayMode` only as a primary surface (P1 fix: opening the
    second screen reset the session to Zoom).
  - It uses the Core tracker for the Audio-mode clear (`IsPresentationSuspended` unchanged) and
    gains `AutoHide` (default true), `IsContentVisible` and `ContentVisibilityChanged`. Hidden
    content is dropped from the frame sink; the shared GPU image visual is hidden.
  - It raises a Warning when the session refuses the frame-sink surface. `PresentationPath` is
    Core's enum.
- **WinForms adapter**:
  - `MediaSurface` presents through a child window it can hide: a disabled, hit-transparent
    child, so mouse input, double-click and drops still reach the `MediaSurface`.
  - Audio mode and auto-hide hide that child and paint `BackColor` (fixes the stale Audio-mode
    frame). New members: `AutoHide`, `IsContentVisible`, `ContentVisibilityChanged`,
    `IsPresentationSuspended`, `PresentationPath` (`NativeWindow`), `PresentationPathReason`,
    `PresentationPathChanged`.
  - The live-resize hint follows the parent form's `ResizeBegin` / `ResizeEnd`. A refused attach
    raises a Warning once.
  - `SecondaryPresentationWindow` (new, Avalonia shape): `(session, surfaceId)`, `Surface`,
    `IsFullScreen`, `ShowOnScreen(Screen?, fullScreen, owner)`, F11 / double-click / Escape.
- **Players**:
  - The WinForms player uses the adapter's `SecondaryPresentationWindow` (its private
    `SecondScreenForm` is gone). Escape leaves full screen, and closes the window when it is not
    full screen (all three adapters' windows behave this way); the "Second screen" box still
    closes it.
  - `--no-auto-hide` moved to the shared `PlayerStartupOptions` (`AutoHideSurfaces`). The
    Avalonia and WinForms players apply it to their surfaces and second screens; the WPF player
    keeps its own parsing of the same flag.
- `scripts/check-presentation-parity.ps1`, changed on purpose:
  - It no longer pins "WPF MediaSurface attaches as Primary": the surface attaches with its
    `Role` (Primary by default) and its effective id.
  - The WPF secondary window must host a `MediaSurface` in its role.
  - All three adapters must expose the shared surface members and the second-screen window
    members.

## Adapter features before and after

| Feature | WPF before | Avalonia before | WinForms before | After (all three) |
| --- | --- | --- | --- | --- |
| `Role` / `SurfaceId` / `EffectiveSurfaceId` / `IsSurfaceAttached` / `IsPrimarySurfaceAttached` | no (always primary) | yes | yes | yes |
| `VideoDisplayMode` forwarded by the primary only | always forwarded | any role forwarded (bug) | yes | yes |
| Audio mode shows the background, not the last frame | no (stale frame) | yes | no (stale frame) | yes (Core `HasPresentation`) |
| `IsPresentationSuspended` | no | yes | no | yes |
| `AutoHide` / `IsContentVisible` / `ContentVisibilityChanged` | yes (adapter policy) | no | no | yes (Core tracker) |
| `PresentationPath` / `PresentationPathReason` / `PresentationPathChanged` | no | yes (adapter enum) | no | yes (Core enum, `NativeWindow` added) |
| Path Info diagnostic owned by the player | no | yes | no | yes |
| Warning when the session refuses the surface | no | no | no | yes |
| Live-resize hint | yes | n/a (composited) | no | yes (WPF, WinForms) |
| DPI change re-sizes the surface | HWND only | yes | yes | yes |
| Adapter notices through `DiagnosticRouter` (owner-aware) | `Debug.WriteLine` | yes | n/a | yes |
| `SecondaryPresentationWindow` in the adapter | yes (no full screen, no placement) | yes | no (test player form) | yes |
| `IsFullScreen`, `ShowOnScreen(screen, fullScreen, owner)`, F11 / double-click / Escape | no | yes | no | yes |
| Monitor list for placement | no | Avalonia `Screens` | WinForms `Screen` | yes (WPF `PresentationScreen`) |

## Decisions

- **Has-presentation on the session, read live.** `HasPresentation` and
  `HasPresentationChanged` are default interface members, so other sessions compile unchanged.
  They follow `Mode` (Audio presents nothing). The tracker also listens to `ModeChanged`, so a
  session that implements only `Mode` and `ModeChanged` still clears. It reads
  `HasPresentation` when asked, not from the event, so a handler that marshals sees the current
  mode: a quick Audio -> Full switch never leaves a surface cleared. This is the Avalonia rule
  from 043A, now for all adapters.
- **One tracker, thread-safe, raising on the changing thread.**
  - Session events arrive on background threads, the present notice on the render thread. The
    tracker applies them under a `System.Threading.Lock` and raises `Changed` outside the lock,
    on that thread, only when its visible state changed; the per-frame path does not allocate.
  - It never calls into the session under its lock.
  - Each adapter coalesces `Changed` with an interlocked flag, marshals once (WPF
    `Dispatcher.BeginInvoke`, Avalonia `Dispatcher.UIThread.Post`, WinForms `BeginInvoke` when
    the handle exists), and reads the properties on the UI thread. On the UI thread itself
    (WPF, WinForms) it applies at once.
  - Adapter events (`ContentVisibilityChanged`, `PresentationPathChanged`) fire on the UI
    thread.
- **029C rules kept.**
  - The latch replays a frame presented before the load notice; it is cleared on hide, close
    and session change.
  - A present while the current item has no visual content never shows the surface.
  - A stop or end that arrives while the session plays again (the internal stop of a
    replacement) is ignored with auto-hide on.
  - Generation counters are replaced by a sender check: an event of a replaced session is
    dropped.
  - A session set while open is armed. The state read after subscribing never undoes an event
    that arrived meanwhile.
  - New: Play or a loop lap after a hide also replays a frame presented while hidden (Core
    presents the stopped position), so resuming needs no second present to show.
- **WPF visibility unchanged otherwise.**
  - With `AutoHide` on, the host shows when the tracker says so (as before). With it off, the
    WPF state rules (playing/paused or a stopped standalone visual) still apply, gated by the
    tracker, which then only removes Audio mode.
  - `IsContentVisible` now also needs the session to present (it is false in Audio mode).
  - The tracker follows the `Player` property, not the load state, so its state survives
    Unloaded/Loaded of the same session (a tab switch, a new parent), as in 029C: a paused video
    stays visible without a new frame, and `BackgroundSurface`/`KaraokeSurface` do not react.
    Core's re-present on re-attach was not relied on (for video it is an asynchronous re-render
    that may not present while paused). The tracker holds the surface weakly, so a dropped,
    unloaded surface with `Player` still set is not kept alive by the session; once it is
    collected the tracker stops following at its next change.
- **Avalonia and WinForms auto-hide defaults to on**, like WPF. This is a visible change for
  those hosts: the surface shows its background until the first frame of the current media,
  and after stop, close, an error or a non-looping end. `AutoHide = false` (players:
  `--no-auto-hide`) gives the old behaviour, except that Audio mode still clears. Avalonia drops
  the frame-sink bitmap when content hides, so a later load never flashes the previous frame.
- **WinForms child window.** The renderer needs a window it owns to hide, without the control
  changing its own `Visible` (the 029C rule: never the outer control's visibility). The
  `MediaSurface` keeps its handle and layout. The child stays hidden (the control paints
  `BackColor`) until the renderer presented into it after each attach and after Audio mode,
  whatever `AutoHide` says, so an unpainted child never shows leftover pixels; nothing paints
  over the swap chain. Its child covers it, is disabled and returns
  `HTTRANSPARENT`, so mouse input (the players' double-click, pointer moves for the full-screen
  chrome) still reaches the surface. The surface paints `BackColor` around and instead of the
  child. The test `HandleCreation_...` now checks the child's handle (`PresentationHandle`,
  internal, `InternalsVisibleTo` the Windows tests).
- **Path enum: no type-forward.** `[TypeForwardedTo]` keeps only the full name, and the
  namespace changes (`FatzBomb.MediaControl.Avalonia` -> `FatzBomb.MediaControl`), so it cannot
  help.
  - Source: compatible for code that has `using FatzBomb.MediaControl;` (every host that
    names `IMediaControlSession` or `MediaControlPlayer` does) or lives under that namespace
    (all repo code).
  - Binary: incompatible for compiled Avalonia consumers; the packages are `0.1.0-alpha` and
    ship in lockstep.
  - Fully qualified `FatzBomb.MediaControl.Avalonia.MediaSurfacePresentationPath` must drop
    `.Avalonia`. The adapter README notes it.
  - Path diagnostic text is now "frame sink path" (was "frame-sink path") in every adapter.
- **WPF second screen hosts a `MediaSurface`.** It replaces its own HWND/composited host code.
  The secondary therefore shares auto-hide, the Audio-mode clear, the live-resize hint, the DPI
  fix, path reporting and the attach warning. `PresentationMode` is set before `Player`, since a
  surface defers a mode change while media is open.
- **Second-screen windows take the session when shown.** The WPF and WinForms windows give
  their surface the session when the window gets its handle (`OnSourceInitialized`,
  `OnHandleCreated`), not in the constructor, so a window that is built but never shown does
  not stay subscribed to the session. Closing clears it. A WPF second screen opened by
  `MediaSurface.ShowSecondaryOutput` follows the opener's `AutoHide`, also when it changes later.
- **WPF placement in device pixels.** `ShowOnScreen` creates the window's handle and moves it
  with `SetWindowPos` (no resize) to centre it in the target monitor's working area. WPF rescales
  it itself when that monitor's DPI differs (per-monitor aware). Full screen is borderless +
  maximized on the window's monitor. WinForms places with `Location` in a per-monitor-V2
  process.
- **Diagnostics without an owner.** A missing `PlatformFactory` or a failed player creation in
  the WPF lightweight controls has no player yet, so it is raised process-wide (every player's
  log reports it). With no player in the process it reaches no log, as before (Debug output).
- **Attach refusal.** Reported once per session (layout retries the attach on every resize); a
  successful attach or a new session re-arms the report.

## Verification (Linux sandbox)

- `EnableWindowsTargeting=true dotnet build FatzBomb.MediaControl.sln -c Release`: 0 warnings,
  0 errors (WPF, WinForms and the Windows tests compile; they do not run here).
- Portable tests: 1463 passed.
- Linux tests (`FATZBOMB_LINUX_FFMPEG_DIR`, `FATZBOMB_LINUX_SAMPLE_VIDEO` set): 101 passed.
- Avalonia headless tests: 39 passed.
- Vulkan tests (lavapipe): 67 passed.
- `dotnet format FatzBomb.MediaControl.sln --verify-no-changes`: clean.
- `check-core-boundary.ps1`, `check-adapter-boundary.ps1`, `check-wpf-boundary.ps1`,
  `check-presentation-parity.ps1`: pass.
- New tests:
  - Portable: `MediaSurfaceContentTrackerTests` (load arms and the first frame shows, latch
    replay, audio-only never shows, stop/end/close/error hide, pause and looping keep, stale
    stop ignored, play after stop re-arms, auto-hide off, Audio mode read live, session
    replacement and dispose unsubscribe, one `Changed` per transition from the render thread,
    `HasPresentationChanged` through a real mode switch, path text).
  - `SurfaceAutoHidePolicyTests` now run portable (the policy is in Core).
  - Avalonia: `AdapterParityTests` (projector never forwards the display mode, auto-hide from
    the first frame to stop, the shared image visual hidden while nothing shows, one refusal
    warning owned by the session); the existing Audio-mode tests pass on the tracker.
- Not run here (Windows CI and the smoke below):
  - `WinFormsMediaSurfaceTests`: child window attach, live-resize hint, Audio mode hides the
    child, auto-hide, refusal warning.
  - `WinFormsSecondaryPresentationWindowTests`: projector attach and detach on close, F11 /
    Escape, centring.
  - `WpfMediaSurfaceParityTests`: dependency property defaults, effective id, projector
    display mode, Audio mode through a real mode switch, the secondary window's surface and
    full screen, centring.
  - No real rendering: native presentation, monitor placement, DPI moves and the live-resize
    effect need the manual smoke.

## Manual smoke (Windows)

Put the x64 BASS and FFmpeg DLLs in each host's `native/` folder. Use a video file, an audio file
with a sidecar image, a plain MP3 and a CDG pair. Two monitors with different scaling help (for
example 100% and 150%).

### WPF player (`FatzBomb.MediaControl.Player`)

1. Start it, open a video. The surface stays black until the first frame, then plays. Stop: the
   surface goes black. Play: the video shows again. Pause: the frame stays.
2. Switch the mode to Audio while the video plays. The surface turns black (no frozen frame);
   the log shows `Wpf.MediaSurface ... cleared, the player is in Audio mode`. Switch back to Full:
   the video shows again at its position.
3. Repeat 2 with `--no-auto-hide`: Audio mode still clears; Full mode shows the video.
4. Choose the presentation mode `WpfComposited` (before loading media), open a video. Move the window
   between monitors with different scaling: the picture stays sharp and fills the surface
   (no blur, no wrong-size frame), the log shows no errors.
5. Back in `NativeHwndOverlay`, tick "Secondary Window" in the flyout. It opens in a window; the log shows
   `Wpf.MediaSurface Surface #n 'secondary': native window path (PresentationMode is NativeHwndOverlay)`.
   Changing the main window's display mode (Zoom / Stretch) changes both screens; the second
   window never resets the main one.
6. In the second window: F11 goes full screen on its monitor, Escape leaves it, double-click
   toggles it. Escape in the window (not full screen) closes it, and the "Secondary Window" box
   unticks. Move it to the other monitor first and repeat: full screen covers that monitor,
   taskbar included.
7. Stop or close the media: the second window goes black too. Switch to Audio mode: it clears.
8. Change the presentation mode with the second window open (no media open): it reopens in the
   new mode in the same place.
9. Resize the main window by dragging its border while a video plays: no errors in the log.
10. Pause a video, then move the surface to another tab or parent (any host that unloads and
    reloads it): the paused frame stays visible, `IsContentVisible` stays true.

### Avalonia player (`FatzBomb.MediaControl.Player.Avalonia`)

1. Open a video: background until the first frame, then video. Stop: background. Play: video.
2. Tick "Second screen", then change the display mode on the main panel: both screens follow;
   opening the second screen no longer resets the main panel to Zoom (P1 fix).
3. Switch to Audio mode and back (as before, 043A): both screens clear, then show video again.
4. `--no-auto-hide`: after Stop the last frame stays (old behaviour); Audio mode still clears.
5. `--surface=framesink` and the default shared-image path both behave as 1-4; the log's path
   line reads `frame sink path` or `shared GPU image path`.

### WinForms player (`FatzBomb.MediaControl.Player.WinForms`)

1. Open a video: black until the first frame, then video. Stop: black. Play: video. Pause: frame
   stays. The log shows `WinForms.MediaSurface Surface #n 'primary': native window path`.
2. Double-click the video: full screen; moving the mouse shows the bars, which hide again after
   2.5 s; double-click or Escape leaves (input still reaches the surface through its child window).
3. Drag a file onto the video area and onto the bars: it loads, as before (drops pass the
   surface's child window).
4. Switch to Audio mode while a video plays: the surface turns black (P1 fix, no frozen frame).
   Back to Full: the video shows again.
5. Resize the window by dragging its border while a video plays: no errors in the log.
6. Tick "Second screen" with two monitors: a borderless window covers the other monitor and shows
   the video; Escape leaves full screen (the window stays), a second Escape closes it and unticks
   the box; F11 or a double-click toggles full screen; unticking the box closes it. With one
   monitor it opens as a 960x540 window.
7. Switch to Audio mode with the second screen open: it clears too. Stop: both go black.
8. `--no-auto-hide`: after Stop the last frame stays on both screens; Audio mode still clears.
   Before the first frame of a file (and when the second screen opens before playback) the
   surfaces show black, never leftover pixels.

## Owner questions (answered by the coordinator, 2026-10-26)

1. The `MediaSurfacePresentationPath` namespace move (binary break for compiled Avalonia
   consumers of the alpha package) is accepted, as documented above.
2. Process-wide diagnostics for the player-less `LightweightPlayerController` failures are fine.
3. `HasPresentation` / `HasPresentationChanged` stay public API (the tracker listens to
   `ModeChanged` only, since review fix 896b397).

## Owner smoke results

Pending.
