# Stage Update

Stage: 027A.5 - Avalonia second-screen output (one player on two screens)
Date: 2026-10-24

## Status: IMPLEMENTED - pending manual smoke

An Avalonia host can now show one player on two screens: the operator view in the main window
and the singer or projector screen on another monitor. Core already fanned out to several
targets; the frame-sink and shared-image paths of the renderers accepted only the primary.
Now every renderer accepts secondary surfaces of both offscreen kinds, each at its own size.
The Avalonia surface also stops showing the last video frame while its player is in Audio mode
(the 043A slice 2 open item).

Delivered:

- **Renderers accept secondary targets.**
  - **Software:** already rendered every composition once per frame-sink target (ENG-016). New
    test with two targets of different shapes; no code change.
  - **Vulkan** (`VulkanRendererBackend`): one target per attached surface, of any role, each a
    frame sink or a shared GPU image consumer with its own exported set and ring. Every
    composition is planned and rendered once per target at the target's size. As in the other
    renderers, a composition succeeds when the primary presented (any target when there is no
    primary), and a target filter (display-locked video, 017A slice 5) is honoured. A target
    detached while a frame is under way is skipped, not failed. A shared-surface resize keeps
    the target and re-exports its set.
  - **Vulkan compositor:** keeps one render target and framebuffer per size (up to 4, least
    recently used goes first) instead of re-creating its single target whenever two surfaces of
    different sizes render in turn. The readback buffer only grows.
  - **Direct3D11** (`D3D11PresentationBackendSet`): offscreen secondaries. A secondary frame
    sink gets its own `D3D11ManagedFrameSinkBackend` (texture plus readback); a secondary
    shared-image surface gets its own `D3D11SharedTextureBackend` (two keyed-mutex textures, the
    slice 2 export). They are enumerated with any primary (offscreen or native) or with none,
    under target ids `offscreen-secondary:<surface id>`, and the composition target filter maps
    them back to their surface ids. One target per surface id: attaching another kind under the
    same id replaces the old target. Native HWND secondaries keep the old rule (suspended while
    an offscreen primary is active). Offscreen secondaries survive a primary detach and are
    re-created with everything else on device loss.
  - **Direct3D11 bridge:** attach and resize route a non-primary frame sink or shared-image
    surface to its secondary target. A secondary that cannot export tells its consumer why
    (`OnUnavailable`) and raises an Info diagnostic.
  - **Core:** no change. Fan-out already composes per target. Frame-sink and shared-image
    surfaces have no vertical-blank timing, so with display-locked timing they join the
    primary's display group (017A slice 5), and the native-video path is already composed for
    every target while a secondary is attached (ENG-015).
- **Avalonia adapter:**
  - `MediaSurface.Role` (`MediaPresentationSurfaceRole`, default `Primary`) and
    `MediaSurface.SurfaceId` (default `primary` for the primary, `secondary` otherwise;
    `EffectiveSurfaceId` gives the one in use). Changing either re-attaches. New
    `IsSurfaceAttached`; `IsPrimarySurfaceAttached` is now true only for the primary role.
  - Each surface picks its own path (shared GPU image or frame sink) and falls back on its own,
    with the reason logged. Diagnostics name the surface id.
  - `SecondaryPresentationWindow` (Avalonia): a `Window` holding a `Projector` surface for a
    session. `ShowOnScreen(screen, fullScreen, owner)` centres it on a chosen `Screen`
    (960x540 DIPs) or covers it. F11 or a double-click toggles full screen; Escape leaves it.
    Closing it detaches the surface. Parity with WPF's `SecondaryPresentationWindow`: same
    default size, title and black background, owned by the host window, placed before it shows;
    full screen and screen choice are new.
  - **Audio mode clear:** the surface follows the session's `ModeChanged` (marshalled to the UI
    thread; it reads the session's current mode, so a late notice after a quick switch back
    does nothing). In Audio mode, which keeps surfaces attached with no renderer, it drops the
    frame-sink bitmap and hides the shared-image visual, so only `Background` shows, and logs
    it. `IsPresentationSuspended` reports it. Frames show again once the mode changes.
- **Avalonia test player:** a **Second screen** box per player. It opens
  `SecondaryPresentationWindow` full screen on the screen given by `--second-screen=<n>`
  (1-based, in Avalonia's screen order), else on the first screen that is not the main
  window's, else (one screen) in a window. `--second-screen=<n>` also opens player 1's second
  screen at startup. The status line adds `2nd screen WxH via <path>` (and `(cleared: Audio
  mode)`), and the log shows the second screen's path and reason.

Bonus, WPF: the WPF `SecondaryPresentationWindow` in `WpfComposited` mode attaches a frame-sink
secondary. Direct3D11 now serves it, so the "not covered" case of ENG-016 (Direct3D11,
composited mode, secondary window blank) is fixed. Smoke step 9 below.

## Decisions made without the owner

- The adapter's role property reuses Core's `MediaPresentationSurfaceRole` (`Primary`,
  `Projector`). No new enum value: Core treats every non-primary role the same.
- A second `MediaSurface` per window rather than one control driving several windows: a host
  puts it in any window, and the helper window is a thin convenience.
- Direct3D11 lets offscreen secondaries present beside a native (HWND/DirectComposition)
  primary. They write only their own textures, so the no-mixed-target reason (swap-chain
  pacing) does not apply. A DirectComposition primary with an offscreen secondary uses the
  final-target composition instead of the overlay split, as with native secondaries.
- Vulkan render targets are cached per size (4) rather than per surface, so two surfaces of one
  size share one target.
- Audio-mode clearing is the only mode rule in the adapter. Closing media in Full mode is
  already cleared by Core, which presents an empty composition.
- The Avalonia test player opens only player 1's second screen at startup; the toggles open
  the others.

## Deferred

- A Vulkan or Direct3D11 composition renders each target separately, even two of the same size
  (as the software renderer does; rare in practice). Still open in ENG-026, to be measured first.
- ~~Vulkan re-uploads the background per target per frame.~~ Fixed by ENG-026 (2026-10-24): one
  upload per background revision, shared by every target.
- ~~Direct3D11 post-process targets of removed secondaries are not pruned.~~ Fixed by ENG-026
  (2026-10-24): released on the next composition.
- Vulkan export on Windows, so a Vulkan player on Windows still uses the frame sink on both
  screens (slice 2 deferral).
- Picking the renderer GPU from the compositor's: a second screen on a monitor driven by another
  GPU falls back to the frame sink with a LUID reason (slice 2 deferral).
- An end-to-end Linux screenshot: the sandbox has FFmpeg but no BASS, and the engine does not
  start without BASS even in Background mode.

## Verification (Linux sandbox)

- `EnableWindowsTargeting=true dotnet build FatzBomb.MediaControl.sln -c Release`: 0 warnings,
  0 errors.
- Tests: Portable 1104, Linux 51, Avalonia (headless) 33, Vulkan (lavapipe) 62. All pass.
  - Vulkan, new `VulkanMultiTargetTests` (5): primary and secondary frame sinks at their own
    sizes with pixel checks, resize and detach of either; the target filter; the per-size
    render-target cache and its limit; a secondary shared-image export beside a primary frame
    sink (re-imported on a second device, pixels checked); primary and secondary exports with
    separate sets and rings (one held, the other presenting).
  - Software, new `TwoTargetsOfDifferentShapes_AreEachComposedForTheirOwnSize`: letterbox on
    one, pillarbox on the other, each equal to a direct render at that size.
  - Avalonia headless, new `SecondScreenTests` (10): two surfaces on one session (ids, roles,
    sizes, independent resize and close); independent sinks; role and id changes re-attach; a
    secondary rejected for shared images falls back alone with its reason; the Audio-mode clear
    for the frame sink (reproduced first: the bitmap stayed), the shared-image visual, and a
    session created in Audio mode; window placement on a screen.
  - Windows only (CI), new `D3D11MultiTargetTests` (5): target-id mapping, no target kept
    without a device, and on WARP a primary and a secondary frame sink at their own sizes with
    pixel checks, and a secondary shared-texture export with resize and detach.
- `dotnet format --verify-no-changes`: clean. `check-core-boundary`, `check-adapter-boundary`,
  `check-presentation-parity` and `check-wpf-boundary` pass.

## Manual smoke (Windows, two monitors, native DLLs in `FatzBomb.MediaControl.Player.Avalonia/native/`)

Run `dotnet run --project FatzBomb.MediaControl.Player.Avalonia -c Release -f net10.0-windows7.0 -- <karaoke video or MP3+G> --renderer=direct3d11 --second-screen=2`.
Keep the log open.

1. **Shared path on both screens.**
   - The second screen opens full screen on monitor 2 and plays in sync with the main window.
   - The status line ends `via shared GPU image | 2nd screen WxH via shared GPU image`, with WxH
     the second monitor's resolution.
   - The log shows `Surface #2 'second-screen-1': shared GPU image path (...)` and a `Shared
     GPU images imported` line for each surface. If either says frame sink, note the reason (a
     monitor on another GPU gives a LUID reason).
2. **CPU.** Compare the CPU % with the second screen open and closed (untick the box), then
   with `--surface=framesink`. The shared path costs one extra GPU composition per frame; the
   frame sink adds a readback and an upload.
3. **Tearing and sync.** Play fast motion and an MP3+G: no torn or flickering frames on either
   screen, and lyrics change at the same moment on both. Pause and seek: both screens update.
4. **Resize.** Press Escape on the second screen (windowed 960x540), drag its edges, maximize
   it, move it between monitors of different DPI, then F11 back to full screen. The picture
   always fills it at the right aspect; the main window is unaffected. The log shows a new
   `Shared textures exported` line per size.
5. **Close and reopen.** Close the second screen with Alt+F4: the box unticks, the main window
   keeps playing, and the log shows no error. Tick the box again: it reopens full screen on
   monitor 2 and plays. Repeat a few times while playing.
6. **Audio and back.** With the second screen open, set the Mode box to Audio. Both surfaces
   go black within a moment (no frozen frame); the log shows `cleared, the player is in Audio
   mode` for each and the status line shows `(cleared: Audio mode)`. Sound continues. Switch
   back to Full: video returns on both screens at the current position, on the same paths as
   before.
7. **Fallbacks.** `--renderer=software` and `--renderer=vulkan`: both screens play `via frame
   sink`, with reasons. Changing the renderer in the toolbar closes the second screen with the
   players; tick it again.
8. **One monitor.** Unplug or disable monitor 2 (or run without `--second-screen`). Ticking the
   box opens a 960x540 window on the main monitor; it plays and resizes.
9. **WPF composited secondary (ENG-016 follow-up).** In the WPF Player with Direct3D11 in
   `WpfComposited` presentation mode, open the secondary output window. It now shows the video
   (before this slice it stayed blank).

Linux (desktop with a GPU, optional): `-f net10.0 -- <video> --renderer=vulkan --second-screen=2`.
Both screens should read `via shared GPU image` on Mesa or NVIDIA; repeat steps 3 to 6.
