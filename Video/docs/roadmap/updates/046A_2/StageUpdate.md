# Stage Update

Stage: 046A.2 - Adapter parity: KaraokeSurface and BackgroundSurface everywhere, WinForms catch-up
Date: 2026-10-26

## Status: IMPLEMENTED - pending manual smoke

Owner decisions 2 and 3 of `active/046A_ACTIVE_AdapterParity.md`: the karaoke and background
controls on every adapter, their rules moved into Core first; WinForms gets a frame-sink fallback
and overlay content. Slice 1 (`updates/046A_1`) is the base.

Delivered:

- **Core** (additive public API):
  - `LightweightPlayerKind` (`Karaoke`, `Background`) and `LightweightPlayerPolicy`: the WPF
    `KaraokeSurface` acceptance rule (CD+G without video, or a MIDI file `.kar/.mid/.midi/.rmi`
    with lyrics; video and plain audio rejected after inspection), the `BackgroundSurface` rule
    (image or video, no CD+G or lyrics), the configuration each kind's player is created with
    (hardware decoding; `MediaPlayerMode.Background` for a background), its settings (karaoke:
    MIDI lyrics on, no loop; background: loop; both Zoom) and when an accepted item starts
    (karaoke always, a background only for video).
  - `LightweightPlayerController`: the WPF `LightweightPlayerController` and the controls'
    reload logic, moved and generalized: one creation shared by concurrent requests (on the
    thread pool), adoption announced by `PlayerChanged` on the caller's context, reload numbers
    (`BeginReload` / `IsCurrentReload`) and the coalescing flag (`TryQueueReload` /
    `ClearQueuedReload`), the load sequence (`LoadAsync`: no sidecars, `PlayOnLoad` off during
    the load, inspect, close a rejected item, start, restore `PlayOnLoad`), `AutoPlay`,
    `NativeLibraryPaths`, `ReleasePlayer`, `Dispose`, transport, `MediaClosed`.
- **WPF adapter**:
  - `KaraokeSurface` and `BackgroundSurface` are thin over the Core controller; same public API
    plus `NativeLibraryPaths` and `BackgroundSurface.LoadAsync(MediaInputSource)` /
    `Source = MediaInputSource`. The internal `LightweightPlayerController` is gone.
  - **Behaviour change (accepted by the owner):** the `BackgroundSurface` player is created in
    `MediaPlayerMode.Background`, so a background never starts the audio engine or an audio
    device monitor (before: a Full-mode player with volume 0). Audio, CD+G and MIDI files are
    rejected by the mode itself (a `PlayerMode` warning in the control's player log).
  - `WpfMediaInputSources.FromImageSource(ImageSource, displayName)` (public static): the
    conversion `Player`'s `ImageSource` overloads used privately, now shared and the same shape
    as the Avalonia and WinForms helpers.
- **Avalonia adapter**: `KaraokeSurface` and `BackgroundSurface` (both `Panel`s, `IDisposable`):
  `Source`, `AutoPlay`, `AutoHide`, `IsContentVisible` (direct property),
  `ContentVisibilityChanged`, `PlatformFactory` (direct property; fixed once the player exists),
  `NativeLibraryPaths`, `Session`, `PreferGpuSharedSurface`, `LoadAsync`, `PlayAsync` /
  `PauseAsync` / `StopAsync`; `CanvasColor` (karaoke); `BackgroundColor`, `SetColor`,
  `OverlayContent`, `OverlayHitTestVisible`, `LoadAsync(Bitmap)`, `LoadAsync(MediaInputSource)`
  (background). The overlay is a `ContentPresenter` above the surface (composited, so above both
  paths).
- **WinForms adapter**:
  - `MediaSurface` frame-sink fallback: when the session refuses the window handle (the software
    renderer) or `PreferNativeWindow` (new, default true) is off, it attaches a managed frame sink
    (`WinFormsFrameSink`) and draws its frames in `OnPaint`, double-buffered. `PresentationPath`
    is `FrameSink` with the reason. Auto-hide and the Audio-mode clear apply as on the window path.
  - `MediaSurface.OverlayContent` (a `Control`) and `OverlayHitTestVisible` (default false):
    shown in `MediaOverlayForm`, a colour-keyed layered tool window owned by the surface's
    top-level form, following the surface and its ancestors, on either path.
  - `KaraokeSurface` and `BackgroundSurface` (plain `Control`s): the members above (with
    `PreferNativeWindow` for the path), `LoadAsync(Bitmap)` on the background.
  - `WinFormsMediaInputSources.FromBitmap(Bitmap, displayName)`.
- **Players**:
  - `--lightweight-surfaces` (shared `PlayerStartupOptions.LightweightSurfacesWindow`): each
    player opens a smoke window with its adapter's `BackgroundSurface` (with an overlay text) and
    `KaraokeSurface`, using the startup renderer and the player's native paths: path box,
    **Browse...**, **Background**, **Karaoke**, **Colour**, **Stop both**.
  - The WinForms player's `--renderer=software` (and `--software`) now uses the software renderer
    (`WindowsMediaControlPlatformFactory` + `SoftwarePresentationCoordinatorFactory`), shown through
    the frame sink, with software decoding (before: a warning and auto). It references
    `Rendering.Software` (DAG updated in `check-core-boundary.ps1`).
- `scripts/check-presentation-parity.ps1`, changed on purpose: every adapter must offer
  `KaraokeSurface` and `BackgroundSurface` over Core's controller and rules with the shared members
  (`AutoPlay`, `AutoHide`, `ContentVisibilityChanged`, `PlatformFactory`, `NativeLibraryPaths`,
  `LoadAsync(string)`, background `OverlayContent` and `LoadAsync(MediaInputSource)`) and a static
  image conversion; the WinForms surface must have the frame sink, its path and `OverlayContent`
  in the overlay window; the WPF surface keeps `OverlayContent`.

## Adapter features after slice 2

| Feature | WPF | Avalonia | WinForms |
| --- | --- | --- | --- |
| `KaraokeSurface` (CD+G, MIDI/KAR, 4:3 on `CanvasColor`) | yes (reworked onto Core) | new | new |
| `BackgroundSurface` (colour, image, silent looping video, overlay) | yes (Background mode now) | new | new |
| Lightweight rules and player helper | Core `LightweightPlayerPolicy` / `LightweightPlayerController` | same | same |
| Lightweight control path option | `PresentationMode` (NativeHwndOverlay / WpfComposited) | `PreferGpuSharedSurface` | `PreferNativeWindow` |
| Lightweight player lifetime | Loaded / Unloaded | attached to / detached from the visual tree | handle created / destroyed |
| `BackgroundSurface.Source` types | `Color`, `SolidColorBrush`, path, `ImageSource`, `MediaInputSource` | `Color`, `ISolidColorBrush`, path, `Bitmap`, `MediaInputSource` | `Color`, path, `Bitmap`, `MediaInputSource` |
| `MediaSurface` paths | `NativeWindow`, `FrameSink` (composited) | `SharedGpuImage`, `FrameSink` | `NativeWindow`, `FrameSink` (new) |
| Path option on `MediaSurface` | `PresentationMode` | `PreferGpuSharedSurface` | `PreferNativeWindow` (new) |
| `MediaSurface.OverlayContent` | yes (layered window, or in-tree when composited) | no (composited: stack it in a `Panel`) | new (layered window on both paths) |
| Static image conversion | `WpfMediaInputSources.FromImageSource` (new public) | `AvaloniaMediaInputSources.FromBitmap` | `WinFormsMediaInputSources.FromBitmap` (new) |
| Player `Load(image)` overloads | `Player.Load(ImageSource)` | `Load(FromBitmap(...))` | `Load(FromBitmap(...))` |
| Software renderer in the test player | yes (composited) | yes (frame sink) | yes (frame sink, new) |
| `--lightweight-surfaces` smoke window | new | new | new |

## Decisions

- **Core owns the rules and the lifecycle, adapters own the framework.** Everything the three
  adapters would copy (acceptance, mode and settings, creation, reload numbering, the load
  sequence, release) is in Core; each adapter keeps its properties, layout, the surface binding
  (`PlayerChanged`) and how it schedules a reload on its UI thread. Avalonia and WinForms share an
  internal `LightweightSurfaceHost` per adapter between their two controls; the public classes
  derive from the framework types as WPF's do (no public base class added).
- **Controller threading.** Members are called on the UI thread. Creation runs on the thread
  pool (it loads native libraries); its adoption is decided under a `System.Threading.Lock` and
  forced to yield, so it never runs inline under the gate. The awaits of `EnsurePlayerAsync` and
  `LoadAsync` resume on the caller's synchronization context on purpose (unlike engine code, which
  uses `ConfigureAwait(false)`), so `PlayerChanged` and the load steps run on the UI thread, as in
  the WPF code they replace. `MediaClosed` arrives on the player's event thread; WPF marshals it.
- **Release abandons everything pending.** `ReleasePlayer` (an unload) and `Dispose` bump the
  lifetime and the reload number. A creation still running then disposes its player on the thread
  pool and is never adopted (WPF's "started while loaded" check, now independent of the
  framework); a load in progress returns false at its next step. A load whose media was accepted
  but which was superseded while it started returns false, so a control never shows a stale load.
  The current player is unbound (`PlayerChanged`) before it is disposed (blocking, as before).
- **Background mode for the background control.** `CreateConfiguration` puts the background's
  player in `MediaPlayerMode.Background`: no audio output, no audio engine, a forced loop (the
  owner accepted this visible change for WPF). Volume is no longer set (there is no audio).
- **A solid colour stays a fill.** `MediaInputSource.FromColor` would need a player and a renderer
  for a colour; every adapter keeps the WPF behaviour (no player, the colour layer or the surface
  background) and releases the player. `MediaInputSource.FromColor` and any other Core source can
  still be loaded explicitly (`Source = MediaInputSource`, `LoadAsync(MediaInputSource)`).
- **Path option per adapter, not `PresentationMode`.** The WPF `PresentationMode` chooses between
  its two hosts; Avalonia and WinForms choose per surface already (`PreferGpuSharedSurface`,
  `PreferNativeWindow`), and their lightweight controls pass that through.
- **Lifetime per framework.** WPF Loaded/Unloaded becomes visual-tree attachment in Avalonia and
  handle creation/destruction in WinForms (a recreated handle reloads the source).
- **WinForms frame sink.** The surface tries the window first and falls back when the renderer
  refuses it (software renderer), so a host needs no renderer knowledge; `PreferNativeWindow =
  false` forces the sink. The sink copies each frame once on the presentation thread into one of
  two alternating buffers (latest wins, nothing allocated per frame) and once on the UI thread into
  a `Format32bppPArgb` bitmap that only the UI thread touches; `OnPaint` fills the background and
  draws it, double-buffered, so no partial frame or flicker is ever shown. A frame that does not
  match the client size (during a resize) is stretched until the next one. Hidden content and
  Audio mode drop the bitmap (a pending frame is kept: it may be the next media's first). One
  `BeginInvoke` per published frame, with a cached delegate.
- **WinForms overlay is always the layered window.** The stage file allowed a child control on
  the frame-sink path. A WinForms child is only "transparent" by repainting its parent, and
  click-through would need per-control hit-test subclassing; one owned layered window works on
  both paths with the WPF semantics (click-through unless `OverlayHitTestVisible`). It is a
  colour-keyed tool window (key RGB 1,0,1: anti-aliased edges blend towards a dark outline rather
  than a bright fringe), owned by the top-level form (MDI children and embedded forms cannot own
  windows), never activated (`WM_MOUSEACTIVATE` returns no-activate, so the owner keeps the keyboard
  focus and its F11 / Escape), shown only while the surface and its form are visible and not
  minimized, and moved on any ancestor's move or resize. It never disposes the host's control:
  the content is taken out before the window is disposed, also when its owner closes it.
- **Image conversions.** All three adapters now have a public static converter with the same
  shape and straight-alpha BGRA output: WPF `WpfMediaInputSources.FromImageSource` (what `Player`
  used privately), Avalonia `AvaloniaMediaInputSources.FromBitmap` (unchanged), WinForms
  `WinFormsMediaInputSources.FromBitmap` (new). Only WPF keeps `Player.Load(ImageSource)`
  convenience overloads; Avalonia and WinForms have no adapter `Player` class (the audit's open
  question; not added here).
- **`NativeLibraryPaths` on the controls** (all three adapters): the players pass their resolved
  native folders, so `--native=` reaches the controls' players without the process-wide `DLLPath`.
- **Smoke windows in every player** (`--lightweight-surfaces`): the WPF player had no host for the
  two controls; one small window per player keeps them testable without a feature solo for one
  player.

## Verification (Linux sandbox)

- `EnableWindowsTargeting=true dotnet build FatzBomb.MediaControl.sln -c Release`: 0 warnings,
  0 errors (WPF, WinForms and the Windows tests compile; they do not run here).
- Portable tests: 1493 passed.
- Linux tests (`FATZBOMB_LINUX_FFMPEG_DIR`, `FATZBOMB_LINUX_SAMPLE_VIDEO` set): 101 passed.
- Avalonia headless tests: 44 passed.
- Vulkan tests (lavapipe): 67 passed.
- `dotnet format FatzBomb.MediaControl.sln --verify-no-changes`: clean.
- `check-core-boundary.ps1`, `check-adapter-boundary.ps1`, `check-wpf-boundary.ps1`,
  `check-presentation-parity.ps1`: pass.
- New tests:
  - Portable `LightweightPlayerControllerTests` (through `TestMediaEngine` and a test platform
    factory): the karaoke and background acceptance tables, configuration and start rules; a
    karaoke CD+G load configures and starts the player; video is closed after inspection; a
    background is a Background-mode, looping player that shows an image without playing and
    rejects audio; `AutoPlay` off; a missing platform factory warns; a failed creation raises one
    Error and allows another attempt; concurrent requests share one creation and one
    `PlayerChanged`; a release during creation disposes that player and the next load creates
    another; a newer load abandons the older; release unbinds before disposing; the reload queue
    coalesces. Run eight times in a row without a failure.
  - `PlayerStartupOptionsTests`: `--lightweight-surfaces`.
  - Avalonia `LightweightSurfacesTests`: the 4:3 karaoke layout and canvas colour, the background
    colour without a player and the overlay presenter above the surface, the missing-factory
    warning, coalesced source changes (one creation attempt, one Error) and the factory change
    after a failed creation, pass-through properties.
- Not run here (Windows CI and the smoke below):
  - `WinFormsMediaSurfaceTests` (extended): the frame-sink fallback with its reason and a frame
    published from another thread into the bitmap, Audio mode and detach dropping it;
    `PreferNativeWindow = false`; the overlay window (owner, bounds, following the form, hiding
    with the surface, content handed back and never disposed); `FromBitmap`.
  - `WinFormsLightweightSurfaceTests`: the 4:3 karaoke layout, the background colour and overlay
    pass-through, the missing-factory warning.
  - `WpfMediaSurfaceParityTests` (extended): `FromImageSource`, the controls' defaults.
  - No real rendering here: the frame-sink paint, the overlay window on screen, the Background-mode
    background without audio and karaoke playback need the manual smoke.

## Manual smoke (Windows)

Put the x64 BASS and FFmpeg DLLs in each host's `native/` folder. Use a video file (MP4), an
image (PNG/JPG), a plain MP3, a CD+G pair or MP3+G ZIP, and a `.kar` file. To check that a
background starts no audio, watch the process in Task Manager / Volume Mixer (no audio session
for the background's player) or the log (no audio device lines for that player).

### WPF player (`FatzBomb.MediaControl.Player`)

1. Start with `--lightweight-surfaces`. The smoke window opens next to the player: black
   background pane with "Overlay above the background", black karaoke pane.
2. Background: enter (or **Browse...**) a video, click **Background**: it plays silently and
   loops; the overlay text stays above it; Volume Mixer shows no audio session from it.
3. **Colour** cycles dark blue, dark green, black: the video stops, the colour shows, the overlay
   stays.
4. Background with an image: it shows. With the MP3: it is not loaded (no sound, no picture of it)
   and the log shows `PlayerMode ... Background mode does not accept audio files`.
5. Karaoke: the CD+G pair or ZIP, **Karaoke**: it plays on a 4:3 area centred in the pane, with
   sound. The `.kar` file: the lyrics show and it plays. The MP4: rejected, nothing plays.
6. Resize the window: both panes follow (the karaoke stays 4:3). **Stop both** stops them.
7. Repeat 2-6 with `--renderer=software` (the controls use `WpfComposited`).
8. Close the main window: the smoke window closes with it, without errors.

### Avalonia player (`FatzBomb.MediaControl.Player.Avalonia`, Windows build)

1. `--lightweight-surfaces --renderer=direct3d11`: the smoke window opens.
2. Repeat WPF steps 2-6. The log's path lines read `shared GPU image path` (or `frame sink path`
   with `--surface=framesink`); the overlay text stays above the video on both.
3. Repeat with `--renderer=software` and with `--renderer=vulkan` (if usable).
4. Close the smoke window, then the main window: no errors.

### WinForms player (`FatzBomb.MediaControl.Player.WinForms`)

1. `--lightweight-surfaces`: the smoke window opens.
2. Repeat WPF steps 2-6. The overlay text is drawn above the background video in its own
   layered window: no coloured fringe box, clicks on it reach the video (double-click on the
   background pane does nothing harmful). Move the smoke window and resize it: the overlay
   follows without lag beyond a frame; minimize and restore: it hides and comes back.
3. Software renderer: start with `--renderer=software` (no `--lightweight-surfaces` needed). The
   log shows `Software renderer forced ...` and `WinForms.MediaSurface Surface #n 'primary': frame
   sink path (the renderer did not accept the window handle ...)`. Open a video: it plays without
   tearing or flicker; resize the window: the picture stretches briefly, then sharpens. Stop:
   black. Switch to Audio mode and back: black, then video. Open the second screen: it uses the
   frame sink too.
4. `--renderer=software --lightweight-surfaces`: repeat step 2 (frame-sink path, overlay still in
   the layered window).
5. Default renderer (Direct3D11): open a video, the log still reads `native window path`
   (unchanged).

## Owner questions (defaults taken; change if wanted)

1. The WinForms overlay uses the layered owned window on both paths (not a child on the
   frame-sink path), with a near-black colour key. Acceptable?
2. Avalonia `MediaSurface` gets no `OverlayContent` (the surface draws the shared GPU image in a
   child composition visual above its children, and hosts stack overlays in a `Panel`); the
   Avalonia `BackgroundSurface` has one. Acceptable, or should the Avalonia surface wrap one too?
3. No adapter `Player` convenience class for Avalonia/WinForms (`Load(Bitmap)` overloads); the
   static converters are the shared shape. Add one in slice 3?

## Owner smoke results

Pending.
