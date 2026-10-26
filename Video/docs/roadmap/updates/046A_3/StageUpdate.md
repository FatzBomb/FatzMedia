# Stage Update

Stage: 046A.3 - Adapter parity: shared player helpers, GPU-first renderer choice, `--players` everywhere
Date: 2026-10-26

## Status: IMPLEMENTED - pending manual smoke

The last slice of `active/046A_ACTIVE_AdapterParity.md` (owner decision 4 and the test-player
items of the audit). Slices 1 (`updates/046A_1`) and 2 (`updates/046A_2`) are the base. With this
slice the whole stage is implemented, pending manual smoke.

Delivered:

- **`FatzBomb.MediaControl.Player.Shared`** (Core only, no UI, adapter, platform or renderer):
  - `Presentation/FullScreenChromeController`: the full-screen chrome rule the three hosts
    duplicated. In full screen the host's bars and cursor hide 2.5 s after the last pointer move
    (`HideDelay`, on the host's `IPlayerTimer`); a real pointer move shows them; a move to the
    position the pointer had when they hid is ignored (the WinForms fix: hiding resizes the picture
    under a still pointer and Windows sends a move); while the host keeps the chrome (pointer on the
    bars, a flyout open, a seek drag) the timer looks again instead of hiding. The host is called
    only on a change (so WinForms' counted `Cursor.Hide`/`Show` stays balanced).
  - `Presentation/PlayerStats.cs`: `ProcessSampler` and `ProcessSample` (moved from the Avalonia
    player), `PresentedFrameCounter` (fps from Core's `PresentationFramePresented`), and
    `PlayerStatsLine` (`Format`, `DescribeSurface` with Core's path names, `WithSecondScreen`).
    `PlayerViewModel.StatsText` / `UpdateStats(ProcessSample, surface)` carry it; the view model
    counts its own player's frames. Each host supplies only the surface description.
  - `Presentation/PlayerScreenChooser`: the second-screen placement as a pure rule over screen
    rectangles (`PlayerScreen`, `PlayerScreenBounds`): `--second-screen=<n>` when it exists, else
    the first screen that is not the main window's (largest overlap; primary when unknown), full
    screen; with one screen a window, saying when the requested screen does not exist.
  - `PlayerRendererRequest` / `PlayerRendererRules`: the neutral renderer names (`auto`, `gpu`,
    `warp`, `software`, `vulkan`, `direct3d11`; aliases `hardware`, `cpu`, `d3d11`), `--software`,
    and the GPU-first `AutoOrder` (documented there and below).
  - `PlayerStartupOptions`: `RendererRequest` / `UnknownRenderer`, `Players` (1..4, clamped),
    `PreferFrameSinkSurface` / `UnknownSurface` (`--surface=`), `SoftwareDecoding` /
    `UnknownDecoding` (`--decoding=`, with `ApplyDecoding`), `SecondScreen`, `ForPlayer(index)`
    and `DescribeUnknownValues()` (the warnings each host logs).
- **WPF player**: `MainWindow` hosts 1-4 `PlayerView`s (the former window content, a
  `UserControl`), one `MainWindowViewModel` each (label `P1`..`P4`); the window owns the
  full-screen window state (borderless, topmost, maximized, the others collapsed), F11 and
  Escape; each view uses the shared chrome controller (its bars move into the surface's overlay
  window as before), shows the stats line, opens its own second screen through the adapter's
  `SecondaryPresentationWindow.ShowOnScreen` on the screen the shared rule picks (projector role,
  the player's presentation mode; reopened when the mode changes), and its seek bar takes focus on a
  click and steps with the keys. `--surface=framesink` starts in `WpfComposited`; `--decoding`,
  `--renderer=vulkan` and `--renderer=direct3d11` work. File drops go to the player under the
  pointer.
- **WinForms player**: `MainForm` hosts 1-4 `PlayerPanel`s (the former form content, a
  `UserControl`; one, side by side, or 2x2), one view model each; the same shared helpers;
  `--surface=framesink` sets `PreferNativeWindow = false` (surface, second screen, lightweight
  controls); drops onto the video work: the panel and the `MediaSurface` both take drops (the
  surface's child window is disabled and transparent to hit tests, so the drop reaches the surface
  on the native-window path, and the frame-sink path paints the surface itself).
- **Windows renderer choice shared by the WPF and WinForms players**:
  `FatzBomb.MediaControl.Player/Configuration/PlayerRendererSelection.cs` (the WinForms project
  compiles it by link) maps a request to a presentation factory, resolves the auto chain once at
  startup off the UI thread, and creates one factory per player. The WinForms player's `auto` was
  Direct3D11 (GPU, else WARP) without the software fallback; it is now the full chain.
- **Avalonia player**: the options are all shared (its `StartupOptions` is a holder of
  `PlayerStartupOptions`; `ProcessSampler` moved out); a WARP entry in the Windows build; `auto` is
  the shared GPU-first order over its probe results (the same first-usable rule as
  `FallbackPresentationCoordinatorFactory`, applied to the probes the toolbar lists), and **the
  default is now `auto`** (it was `software`); an unusable choice falls back to auto with a warning;
  `--hdr-output` is logged as not available and ignored; the seek slider steps 5 s / 10 s like the
  others.
- **Avalonia and WinForms adapters**: a thin `Player : MediaControlPlayer` like `Wpf.Player`
  (`CreateAsync(configuration, nativeLibraryPaths, platformFactory)` on the thread pool and
  `Load`/`LoadAsync` overloads for the framework's `Bitmap`). The players create their engines
  through it.
- **Scripts**: `check-core-boundary.ps1` adds `Rendering.Vulkan` to the WPF and WinForms players'
  references (on purpose); `check-presentation-parity.ps1` pins the three adapter `Player`
  facades and, in every player, the shared chrome controller, screen rule, stats line,
  `--second-screen`, `ProcessSampler`, `--players` and the shared renderer request.

## `--renderer=auto` per platform

| Platform / player | Order tried (first usable wins) | Decoding |
| --- | --- | --- |
| Windows, WPF and WinForms | Direct3D11 on the GPU, Direct3D11 on WARP, software (`FallbackPresentationCoordinatorFactory(Direct3D11 Auto, Software)`) | hardware on a GPU, software on WARP or software |
| Windows, Avalonia (`net10.0-windows7.0`) | Direct3D11 (GPU), WARP, software, over the toolbar's probes | auto (D3D11VA) on a GPU, software otherwise |
| Linux (and other non-Windows), Avalonia | Vulkan, software | software (the Vulkan and software renderers take no hardware frames) |

Vulkan is never chosen automatically on Windows; `--renderer=vulkan` asks for it in every player.

## Decisions

- **Factory creation stays in the hosts.** Player.Shared names the request and the order; a host
  maps it to the factories it references and logs the result (Renderer lines in each player's log).
  WPF and WinForms share one mapping file by link rather than a new project: both are Windows
  hosts with the same renderers, and a renderer-referencing shared project would be a new DAG node
  for one file.
- **One factory per player.** The auto chain is resolved once (it creates a probe device) and
  each player gets a fresh factory of the chosen kind.
- **Players are fixed at startup in WPF and WinForms.** `--players=` and `--renderer=` are read at
  startup; the Avalonia player's toolbar can also change both at run time (it recreates the
  players). See open questions.
- **Second screen owned by the player in WPF.** The WPF player no longer uses
  `MediaSurface.ShowSecondaryOutput()` (it stays in the adapter): it places the adapter's window
  itself with `ShowOnScreen`, like the other players, so `--second-screen=` and the placement rule
  are the same everywhere.
- **The `Player` facade in Avalonia and WinForms is added, without a default platform.** It stays
  thin (constructor, `CreateAsync`, `Bitmap` overloads), so app code reads the same on every
  adapter: `await Player.CreateAsync(configuration, paths, platformFactory)`, `player.Load(bitmap)`.
  A default platform factory is not clean here: the Avalonia adapter is a single `net10.0` assembly
  and would have to multi-target and reference both the Windows and the Linux platform packages
  (the player's `PlatformComposition` does that per TFM); the WinForms adapter depends on Core
  only. WPF keeps its `IMediaPresentationCoordinatorFactory` overload (Windows platform by default)
  because it can present only on Windows.
- **Chrome rule details.** All players now keep the chrome while a flyout is open (Avalonia did;
  WPF hid it) and ignore a pointer "move" to the same place (WinForms did).
- **Stats state text** is the engine state (`Playing`, `Stopped`, `Null` before media), as the
  Avalonia line showed.

## Parity after stage 046A

Adapters (`FatzBomb.MediaControl.Wpf`, `.Avalonia`, `.WinForms`):

| Feature | WPF | Avalonia | WinForms | Note |
| --- | --- | --- | --- | --- |
| `MediaSurface` with Core auto-hide, Audio-mode clear, path reporting, `Role` / `SurfaceId` | yes | yes | yes | |
| `SecondaryPresentationWindow` (`ShowOnScreen`, `IsFullScreen`, F11, double-click, Escape) | yes | yes | yes | |
| `KaraokeSurface`, `BackgroundSurface` over Core's controller | yes | yes | yes | |
| Static image conversion | `FromImageSource` | `FromBitmap` | `FromBitmap` | each framework's image type |
| `Player` facade (`CreateAsync`, image `Load` overloads) | yes | yes (new) | yes (new) | default platform only in WPF: the only Windows-only presenting adapter (see decisions) |
| Native window path (HWND swap chain; HDR output) | yes | no | yes | platform limit: Avalonia composites everything (no airspace); HDR output needs a native Direct3D11 window |
| Shared GPU image path (no CPU copy into the UI) | no (deferred) | yes | no (native window instead) | WPF D3DImage path deferred by the owner (large, high risk); WinForms presents natively |
| Frame-sink path | yes | yes | yes | |
| `MediaSurface.OverlayContent` | yes | no (stack in a `Panel`) | yes | Avalonia composites the surface in the visual tree, so any sibling is already above it (slice 2 owner question) |
| Path option | `PresentationMode` | `PreferGpuSharedSurface` | `PreferNativeWindow` | each framework's own paths |

Players (`FatzBomb.MediaControl.Player`, `.Player.Avalonia`, `.Player.WinForms`):

| Feature | WPF | Avalonia | WinForms | Note |
| --- | --- | --- | --- | --- |
| Shared `PlayerViewModel` and every flag of `PlayerStartupOptions` | yes | yes | yes | |
| `--players=1..4` (one view model each) | yes (new) | yes | yes (new) | |
| `--renderer=auto` GPU first | yes | yes (new; default now auto) | yes (software fallback new) | |
| `--renderer=gpu/software/vulkan` | yes (vulkan new) | yes | yes (vulkan new) | |
| `--renderer=warp/direct3d11` | yes | Windows build (WARP new) | yes | platform limit: Direct3D11 is Windows only |
| `--surface=auto/framesink` | yes (new) | yes | yes (new) | |
| `--decoding=auto/software` | yes (new) | yes | yes (new) | |
| `--second-screen=<n>` and the shared placement rule | yes (new) | yes | yes (new) | |
| `--hdr-output` | yes | warns, ignored | yes | platform limit: no native window surface in Avalonia |
| Full-screen chrome (shared controller) | yes | yes | yes | |
| Stats line (fps, CPU, memory, surface path) | yes (new) | yes | yes (new) | |
| Seek bar keys (5 s / 10 s) | yes (new) | yes | yes | |
| File drop onto the player and its video | yes | yes | yes (surface takes drops) | |
| `--lightweight-surfaces` smoke window | yes | yes | yes | |
| Change renderer / player count at run time | no | toolbar | no | open question below; all three take both on the command line |
| Flyouts | flyouts | flyouts | tabs | the same panels, WinForms' layout |

## Verification (Linux sandbox)

- `EnableWindowsTargeting=true dotnet build FatzBomb.MediaControl.sln -c Release`: 0 warnings,
  0 errors (WPF, WinForms and the Windows tests compile; they do not run here).
- Portable tests: 1529 passed (new `PlayerPresentationHelperTests`: renderer names, `--software`
  and invalid values, the auto order, `--players` clamping, `--surface` / `--decoding` /
  `--second-screen` and their warnings, `ForPlayer`, the chrome controller with a fake timer
  (delay, synthetic move ignored, real move shows, keep-and-retry, exit, dispose), the screen
  rule (requested, other screen, one screen, none, owner by overlap, names), the stats line
  format, the frame counter with a fake clock, the process sampler).
- Linux tests (`FATZBOMB_LINUX_FFMPEG_DIR`, `FATZBOMB_LINUX_SAMPLE_VIDEO`): 101 passed (one
  timing test failed once and passed on the rerun; nothing in it changed here).
- Avalonia headless tests: 44 passed. Vulkan tests (lavapipe): 67 passed.
- `dotnet format FatzBomb.MediaControl.sln --verify-no-changes`: clean. The four boundary scripts
  pass.
- Avalonia player under Xvfb (no window manager), `--players=2` with the sample video: both
  players start, `auto` chose Vulkan (lavapipe), player 1 plays, the stats lines read e.g.
  `P1 Playing | 30.0 fps | CPU 20% ... | surface 622x409 via frame sink`; F11 showed player 1
  alone, the chrome hid after the delay, a real pointer move showed it again, Escape restored both
  players. With `--renderer=warp --hdr-output --decoding=fast --surface=framesink` the log shows the
  three warnings (unknown decoding, WARP not usable here so auto, `--hdr-output` ignored).
- Not run here: the WPF and WinForms players, Direct3D11, WARP, real second screens, drops, the
  Windows build of the Avalonia player. See the smoke steps.

## Manual smoke (Windows)

Put the x64 BASS and FFmpeg DLLs in each host's `native/` folder. Media used:
`C:\media\video.mp4` (H.264), `C:\media\song.mp3`, `C:\media\karaoke.zip` (MP3+G),
`C:\media\hdr10.mkv` (HDR10, optional). Two monitors for the second-screen steps (one monitor:
the second screen opens as a window).

### WPF player (`FatzBomb.MediaControl.Player`)

1. `--players=2 C:\media\video.mp4`: two players side by side; P1 plays the video, P2 is empty.
   Each has a stats line (`P1 Playing | ... fps | ... | surface WxH via native window`). The Log
   panel's Renderer lines show the auto chain (`Direct3D11: available ... (selected)`).
2. Drop `C:\media\song.mp3` onto P2's picture: it plays there, P1 unchanged.
3. Double-click P1's picture: P1 alone, full screen, controls at the bottom; leave the mouse
   still: after about 2.5 s the controls and the cursor hide; move the mouse: they come back;
   rest the pointer on the controls: they stay; open the Log flyout: they stay. Escape closes the
   flyout, Escape again leaves full screen and both players show.
4. Click P1's seek bar, then Right / Left (5 s), Page Up / Page Down (10 s), Home / End.
5. A/V flyout, **Secondary Window** on P1: with two monitors it opens full screen on the other
   one (log: `P1 second screen opens full screen on screen 2 of 2 (...)`); the stats line gains
   `| 2nd screen WxH via native window`. Untick: it closes.
6. Restart with `--second-screen=2 C:\media\video.mp4`: P1's second screen opens on screen 2 at
   startup. `--second-screen=5`: a window, and the log says screen 5 does not exist (one-screen
   case) or the other screen is used.
7. `--surface=framesink`: the surface paths read `frame sink` (WpfComposited). `--decoding=software`:
   the log has `Video decoding: software decoding (--decoding=software).`
8. `--renderer=warp`, `--renderer=software`, `--renderer=vulkan`, `--renderer=direct3d11`,
   `--renderer=bogus`: each plays `C:\media\video.mp4`; the log names the choice (bogus: a warning,
   auto).
9. `--players=4`: a 2x2 grid of four independent players; close the window: it closes without
   errors.

### Avalonia player (`FatzBomb.MediaControl.Player.Avalonia`, `-f net10.0-windows7.0`)

1. No `--renderer`: the toolbar shows `Direct3D11` selected (was Software before this slice); the
   Renderer line reads `renderer Direct3D11: Direct3D11 composition ..., hardware decoding (D3D11VA)`;
   the stats line reads `via shared GPU image`.
2. The renderer box lists Software, Vulkan, Direct3D11 and Warp; choose Warp: the players are
   recreated and play with software decoding.
3. `--hdr-output`: the log warns that it is not available here; playback is unaffected.
4. Repeat WPF steps 2-6 and 9 (`--players=2`, drop, full screen and chrome, seek keys, second
   screen with `--second-screen=`).

### WinForms player (`FatzBomb.MediaControl.Player.WinForms`)

1. `--players=2 C:\media\video.mp4`: two panels side by side, each with its tabs, status and
   stats line; the Renderer lines show the auto chain (new: the software fallback is part of it).
2. Drag `C:\media\song.mp3` from Explorer onto P2's video area (not the bars): the cursor shows
   the copy effect and it plays in P2. Repeat with `--renderer=software` (frame-sink path).
3. Double-click P1's video: P1 alone, borderless, maximized; still mouse: bars and cursor hide
   after 2.5 s and do not come back by themselves (no flicker loop); move the mouse: they show;
   rest on the Full screen button: they stay. Escape leaves full screen; both panels show again.
4. **Second screen** on P1 with two monitors: full screen on the other monitor; with
   `--surface=framesink` its path reads `frame sink`. `--second-screen=2`: opens at startup.
5. `--renderer=vulkan`: plays through the frame-sink path (or, without a Vulkan driver, a warning
   and auto). `--decoding=software`: the decoding line in the log.
6. `--players=3`: three panels (2x2 with one empty cell); close: no errors, the cursor is visible.

## Open questions (defaults taken)

1. Runtime renderer / player-count switching exists only in the Avalonia player's toolbar (it
   predates this stage). WPF and WinForms take both on the command line. Add the toolbar to them,
   or keep it as the Avalonia player's renderer-comparison tool?
2. The Avalonia player's default renderer is now `auto` (GPU first), as the owner's rule says; it
   was `software`. Keep?
3. `--renderer=gpu` in Avalonia's Windows build is Direct3D11 on the GPU; on Linux it is Vulkan.
   `warp` and `direct3d11` off Windows fall back to auto with a warning.
