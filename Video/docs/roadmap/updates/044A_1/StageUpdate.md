# Stage Update

Stage: 044A.1 - Test player parity on a shared view model
Date: 2026-10-26

## Status: IMPLEMENTED - pending manual smoke

Owner decision (2026-10-26): the WPF, Avalonia and WinForms test players match, through one
UI-agnostic view model; the WPF player is the reference. See
`active/044A_ACTIVE_TestPlayerParity.md` for the acceptance criteria.

Delivered:

- `FatzBomb.MediaControl.Player.Shared` (net10.0, Core and CommunityToolkit.Mvvm only):
  - `ViewModels/PlayerViewModel` and its partials (`.Seek`, `.Status`, `.Modes`, `.Network`,
    `.PreparedNext`, `.Equalizer`, `.Subtitles`, `.VisualTransforms`, `.Log`): the WPF player's
    view model, written against `MediaControlPlayer`.
  - `Hosting/`: `IPlayerDispatcher` (UI thread and its timer), `IPlayerClipboard`,
    `IPlayerFilePicker`, `IPlayerEngineFactory` (`ResolveRendererAsync`, `CreatePlayerAsync`,
    native paths, the native report after a failure) and `PlayerRendererInfo`, bundled in
    `PlayerHost`; `PlayerDebugLog` (the debug log file) and `PlayerNativePaths`.
  - `PlayerStartupOptions`: the shared command line (below).
- WPF player: `MainWindowViewModel` derives from `PlayerViewModel` and keeps the WPF-only
  presentation mode and surface auto-hide. XAML and look unchanged, plus a PLAYER mode box in the
  A/V flyout.
- Avalonia player: one `PlayerViewModel` per panel, bound in XAML (compiled bindings), with the
  WPF flyouts.
- WinForms player: the same view model through a small `ViewModelBinder`; the flyouts are tabs.
- WinForms adapter: `MediaSurface.Role` and `SurfaceId` (additive; default primary), for the
  WinForms second screen.
- `scripts/check-core-boundary.ps1` pins the new project (Core only) and checks that it keeps a
  portable TFM, only the MVVM package, and no UI-framework namespaces.

## Feature parity

| Feature | WPF | Avalonia | WinForms |
|---|---|---|---|
| Open a file (dialog with the engine's filter) | yes | yes | yes |
| Drop a file to open | yes | new | new |
| Startup file, `--url=` or positional URL | yes (positional URL new) | yes | new |
| URL box (http/https, Enter opens), stream info | Next flyout | top bar + Next | top bar + Next tab |
| Play, Pause, Stop | 3 buttons | Play/Pause + Stop | 3 buttons |
| Loop | yes | yes | new |
| Seek bar scrubbing while dragging (`BeginScrub`/`EndScrub`) | yes | new (was seek on release) | new (was seek on release) |
| Key step on the seek bar | - | yes | yes |
| Elapsed / remaining time, volume | yes | yes | yes |
| Pitch, tempo | Audio | Audio | Audio tab (new) |
| Normalize, reset pitch/tempo on load, silent drag | Audio | new | new |
| 10-band equalizer, Flat, `--eq=` | Audio | new | new |
| A/V, CD+G and subtitle sync offsets, reset | Sync | new | new |
| Subtitle track picker | Sync | new | new |
| Visual transforms (hue ... blue, include overlays, reset) | Video | new | new |
| Auto play, display-locked timing, display mode, CD+G background | A/V | new | new |
| Player mode (Full, Background, Audio; `--mode=`) | new (A/V PLAYER) | yes | new |
| Prepared next, auto-next, fade length, fade out/in/cancel | Next | new | new |
| Log: newest first, coloured levels, copy, clear, open log file | Log | copy existed; rest new | new (was a plain list) |
| Debug log file in `%TEMP%\FatzBombMedia` | `player-debug.log` | new: `player-avalonia-debug.log` | new: `player-winforms-debug.log` |
| Status line (latest status, warnings and errors) | yes | new (stats line kept) | yes |
| Full screen (F11, double-click, Escape) with auto-hiding controls | yes | new | new |
| Second screen | Secondary Window | Second screen box | new (Second screen box) |
| Several players in one window | - | 1-4 (`--players=`) | - |
| Renderer choice | `--renderer=auto/gpu/warp/software` | `--renderer=software/vulkan/direct3d11/auto`, toolbar | new: `--renderer=auto/gpu/warp` |
| Presentation mode, surface auto-hide | WPF only | - | - |
| Surface path, `--surface=framesink`, process sampler | - | Avalonia only | - |

## Command line

Shared (`PlayerStartupOptions`, every player):

| Flag | Meaning |
|---|---|
| `<file>` | first argument that is not a flag: loaded at startup (an http/https URL here is a stream) |
| `--url=<http(s)://...>` | stream loaded at startup; replaces a positional file |
| `--mode=full\|background\|audio` | mode the player is created in |
| `--native=<dir>` (repeatable) | native library folders searched first |
| `--net-retries=<n>`, `--net-ca-file=<pem>`, `--net-allow-invalid-certs` | network policy |
| `--no-hdr-tonemap`, `--hdr-tonemap-cpu` | HDR comparison modes |
| `--trim-silence`, `--reset-pitch-on-load`, `--reset-tempo-on-load` | engine options |
| `--eq=g1,...,g10` | equalizer gains (turns it on) |
| `--prepare-next=<file>`, `--auto-next`, `--fade-next=<s>` | prepared next item |
| `--visual-transform-demo` | visual transforms start at a demo setting |
| `--renderer=<name>`, `--software` | renderer; each host maps the name to its renderers |

Host flags: WPF `--no-auto-hide`; Avalonia `--players=`, `--surface=`, `--second-screen=`.
Every flag the players had keeps working. Changes: only http/https `--url=` values are streams
(the WPF player took any absolute URI), and a URL replaces a positional file (the WPF player
loaded both at once); the WinForms player now reads the whole command line.

## Decisions

- **One view model, host abstractions.** Hosts supply small interfaces (`Hosting/`); the view
  model builds the engine configuration from the shared options and owns everything after
  creation. A host-specific need is a derived view model (WPF presentation mode, auto-hide) or
  view code (surfaces, layout, full screen, second screen, drops).
- **The WPF move is behaviour-neutral** (commit 1). Later commits change the shared view model
  for every host, deliberately:
  - **Status reads off the UI thread** (`State`, `Duration`, `Position` on the thread pool every
    200 ms, a tick skipped while a read is out; slow, blocked or failed reads are logged), as
    the Avalonia player did for ENG-035. The WPF player read them on the UI thread.
  - **Scrub calls off the UI thread.** `BeginScrub` and `EndScrub` wait for the control queue (a
    network seek takes seconds), so they run in order on a "scrub lane" on the thread pool. A
    drag seek made while the lane is busy waits for it (only the newest target), so it never
    overtakes its drag's `BeginScrub` or the previous release. The release seek is `SeekAsync`
    (was a blocking `Seek` on the WPF UI thread); it is normally a repeat of the last drag
    target, which the engine ignores.
  - **Dispose** clears `Player` first (bound surfaces let go, commands turn off); `DisposeAsync`
    disposes off the UI thread and, while the engine is still starting, waits for it.
- **Engine configuration** comes from the view model for every host: logging on, the network,
  HDR, trim and load-reset options. The WinForms player used `MediaPlayerConfiguration.Auto`
  (logging off); the Avalonia player did not pass those options.
- **Startup media**: one item; only the first Avalonia player gets it (first build only).
- **Avalonia layout**: flyout toggle buttons as in WPF, the Log flyout open at start (the log
  was always visible); Mode and Second screen stay on the transport row; full screen shows only
  that player. A click on the picture closes the open flyout, as in WPF.
- **WinForms layout**: tabs instead of flyouts. Its HWND surface needs Direct3D11, so
  `--renderer=software` falls back to auto with a warning.
- **WinForms second screen** needed the adapter to attach a non-primary surface: `Role` and
  `SurfaceId` (as in the Avalonia adapter); a non-primary surface does not forward
  `VideoDisplayMode`.

## Verification (Linux sandbox)

- `EnableWindowsTargeting=true dotnet build FatzBomb.MediaControl.sln -c Release`: 0 warnings,
  0 errors (WPF and WinForms players build, not run).
- Tests: Portable 1404 pass (59 new: `PlayerViewModelTests`, `PlayerStartupOptionsTests`);
  Linux 94 pass (with FFmpeg 9 and the sample video); Avalonia 33 pass; Vulkan 67 pass. The 3
  new `WinFormsMediaSurfaceTests` (role, surface id) compile but need Windows.
- `dotnet format --verify-no-changes`: clean. `check-core-boundary.ps1`,
  `check-adapter-boundary.ps1`, `check-wpf-boundary.ps1`, `check-presentation-parity.ps1`: pass.
- Avalonia player under Xvfb (software and Vulkan/lavapipe, FFmpeg only, no BASS): played the
  sample video; every flyout rendered; a seek drag scrubbed (11 fps of previews while
  dragging) and played on from the release point; the red gain removed red; Mode switched to
  Audio (no BASS: the engine's error, as expected) and Background (loop shown on); the URL box
  played a local HLS stream; Copy all; two players with the startup file in player 1 only; full
  screen hid the controls after 2.5 s; Escape closed the flyout, then full screen; the second
  screen opened in a 960x540 window.
- Not verified: anything on Windows (WPF and WinForms at run time, Direct3D11, BASS audio, the
  Windows clipboard and dialogs), real full screen and second screens (Xvfb has no window
  manager).

## Manual smoke (Windows)

Setup: the x64 BASS and FFmpeg 9 DLLs in each player's `native/` folder
(`docs/NATIVE_DEPENDENCIES.md`). Test files: a music video (MP4), an MP3+CDG pair, a video with
a sidecar `.srt`, a second MP3. From the repo root:

```powershell
dotnet build FatzBomb.MediaControl.sln -c Release
$video = 'C:\Media\video.mp4'; $next = 'C:\Media\song.mp3'
$hls = 'https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8'
```

1. **WPF regression pass (the reference).**
   `dotnet run --project FatzBomb.MediaControl.Player -c Release -- $video`
   1. The video plays after startup, as before. Open (button), then drop the MP3+CDG on the
      window: each loads and plays; lyrics show.
   2. Drag the seek bar slowly across the video: the picture follows the drag (preview
      frames), sound is muted while moving (Silent Drag on) and comes back when the drag rests
      or is released; playback goes on from the release point. Untick Silent Drag (Audio
      flyout) and drag again: sound follows the drag. Click on the bar: it jumps there.
   3. Every flyout works as before: Sync (A/V, CDG, SUB +/-, Reset, the subtitle picker with
      the `.srt` video), Video (sliders, Include Overlays, Reset Visual), Audio (pitch, tempo,
      Normalize, the reset boxes, EQ and Flat), A/V (Auto Play, Auto Hide, Secondary Window,
      Display-Locked, DISPLAY, MODE before any media is loaded, BG), Next (Choose..., Play next
      now, Clear, Auto-next, FADE, Fade out/in, Cancel, URL Open), Log (Copy all, Clear, Open
      log file opens `%TEMP%\FatzBombMedia\player-debug.log`).
   4. New: A/V flyout **PLAYER** box. While the video plays switch Full -> Audio -> Full: after
      a short gap each time the sound goes on at the same position and the picture comes back.
      Switch to Background: the picture plays without sound and the Loop button turns on;
      back to Full, Loop returns to its earlier state.
   5. Full screen (double-click, F11, Esc): controls overlay and auto-hide as before.
   6. `dotnet run --project FatzBomb.MediaControl.Player -c Release -- --software $video`:
      WPF composited, software renderer, as before.
2. **Network and the UI thread (all three players).**
   `dotnet run --project FatzBomb.MediaControl.Player -c Release -- --url=$hls --net-retries=3`
   (then the same with `FatzBomb.MediaControl.Player.Avalonia -c Release -f net10.0-windows7.0
   -- --renderer=direct3d11 --url=$hls`, and `FatzBomb.MediaControl.Player.WinForms -c Release
   -- $hls`). The stream plays; drag the seek bar quickly back and forth ten times, and once
   more before the previous seek has shown its frame. The window stays responsive throughout
   (move it, drag the volume slider); each drag ends playing from its release point. The Log
   shows the `Network` lines and no `Status read blocked` warning.
3. **Shared flags (each player).** Run with
   `-- --eq=6,3,0,0,0,0,0,0,3,6 --prepare-next=$next --auto-next --fade-next=3 --reset-pitch-on-load --visual-transform-demo $video`:
   EQ is on with those gains, the Next panel shows the prepared MP3 (ready), Reset pitch on
   load is ticked, the transforms show ACTIVE, and when the video ends it fades into the MP3.
   `-- --mode=audio $video` starts in Audio mode (sound only); `-- --native=C:\Other\native`
   lists that folder first in the Log's native lines.
4. **Avalonia parity.**
   `dotnet run --project FatzBomb.MediaControl.Player.Avalonia -c Release -f net10.0-windows7.0 -- --renderer=direct3d11 --players=2 $video`
   1. Player 1 plays the video, player 2 is empty. In each player, open every flyout and
      repeat step 1.3's checks (Sync, Video, Audio, A/V, Next, Log); all six work.
   2. Seek-bar drag scrubs as in WPF (step 1.2); arrow keys on the focused bar step through
      the media (one seek each).
   3. Mode box, Second screen (opens full screen on the second monitor), Copy all, the stats
      line (fps, CPU, surface path) work as before; `--surface=framesink` still forces the
      frame sink.
   4. Double-click player 2's picture: it fills the screen alone; the controls hide after
      2.5 s without mouse movement and come back on movement; Esc leaves full screen.
   5. Drop a file on player 2: it loads there.
5. **WinForms parity.**
   `dotnet run --project FatzBomb.MediaControl.Player.WinForms -c Release -- $video`
   1. The video plays; the tabs Sync, Video, Audio, A/V, Next and Log reproduce step 1.3's
      checks (EQ sliders: up is boost).
   2. Drag the seek-bar thumb: scrubbing as in WPF; click on the bar: it jumps there; Alt+Tab
      during a drag ends it (sound comes back).
   3. Play, Pause, Stop, Loop, Mode (Full/Background/Audio), Volume, drop a file, the URL box
      (`$hls`, Enter).
   4. Second screen: with two monitors the picture fills the other one (Esc on it, or untick,
      closes it); with one, a 960x540 window. Full screen: F11, double-click, Esc; the controls
      hide after 2.5 s.
   5. `-- --renderer=warp $video` plays on WARP (software decoding; the Log says so);
      `-- --renderer=software` logs a warning and uses auto.
   6. Close the window while a video plays, and once while it is still starting: it closes
      within a second or two, without an error dialog.
