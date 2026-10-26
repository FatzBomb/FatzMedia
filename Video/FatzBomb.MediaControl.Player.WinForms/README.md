# FatzBomb.MediaControl.Player.WinForms

WinForms test player for `FatzBomb.MediaControl`. Windows only (`net10.0-windows7.0`, x64).

## What it tests

- The `FatzBomb.MediaControl.WinForms` adapter: an HWND `MediaSurface` bound to a `MediaControlPlayer`
  (attach, resize, DPI, live resize, auto-hide, the Audio-mode clear, detach on close), and the
  adapter's `SecondaryPresentationWindow` (a projector `MediaSurface`) for the second screen
  (Stage 046A).
- The Windows + Direct3D11 platform (`WindowsDirect3D11MediaControlPlatformFactory`) in a non-WPF host.
- The shared `PlayerViewModel` (`FatzBomb.MediaControl.Player.Shared`, Stage 044A) through plain
  WinForms bindings (`ViewModelBinder`): the WPF player's features, with its flyouts as tabs.
  It runs in WinForms' dark mode (`Application.SetColorMode`), like the other players.

## Features

- Top bar: **Open...** (dialog filtered by the engine), what is loaded, the URL box (http/https;
  Enter or **URL...** opens it). A file dropped on the window opens too.
- Seek bar: dragging scrubs (`BeginScrub`, a seek per move, `EndScrub`; silent while moving when
  **Silent drag** is on), a click jumps there, arrow keys step 5 s. Elapsed and remaining time.
- **Play**, **Pause**, **Stop**, **Loop**, **Mode** (Full, Background, Audio; switches on the fly),
  **Second screen** (full screen on another monitor, else a 960x540 window; in it F11 or a
  double-click toggles full screen; Escape leaves it, or closes the window when it is not full
  screen), **Volume**,
  **Full screen** (also F11 or a double-click on the picture; Escape leaves it; the controls hide
  after 2.5 s without mouse movement).
- Tabs: **Sync** (A/V, CD+G, subtitle offsets, reset, subtitle picker), **Video** (visual
  transforms), **Audio** (pitch, tempo, normalize, load resets, silent drag, 10-band EQ, Flat),
  **A/V** (auto play, display-locked timing, display mode, CD+G background), **Next** (prepared
  item, play next now, clear, auto-next, fade length, fade out/in/cancel, stream info), **Log**
  (every diagnostic, newest first, coloured by level; Copy all, Clear, Open log file).
- A status line with the latest status, warning or error. Every diagnostic also goes to
  `%TEMP%\FatzBombMedia\player-winforms-debug.log`, overwritten at each start.

## Run

```powershell
dotnet run --project FatzBomb.MediaControl.Player.WinForms -c Release -- [media-file|http(s)-url] [options]
```

Options: `--renderer=auto|gpu|warp` (Direct3D11: the GPU, else WARP; the GPU only; WARP with
software decoding; the HWND surface cannot use the software renderer, so `software` falls back to
auto with a warning) and the flags every test player shares (`PlayerStartupOptions`): `--url=`,
`--mode=`, `--native=<dir>`, `--net-retries=`, `--net-ca-file=`, `--net-allow-invalid-certs`,
`--no-hdr-tonemap`, `--hdr-tonemap-cpu`, `--hdr-output`, `--trim-silence`, `--reset-pitch-on-load`,
`--reset-tempo-on-load`, `--eq=`, `--prepare-next=`, `--auto-next`, `--fade-next=`,
`--visual-transform-demo`, `--no-auto-hide` (surfaces keep the last frame after stop; Stage 046A).

## Native libraries

The packages ship no BASS or FFmpeg binaries. Put the x64 DLLs (BASS, BASS_FX, FFmpeg 9.x) in

```text
FatzBomb.MediaControl.Player.WinForms/native/
```

They are copied next to the app at build/publish time (`native/` under the app folder; the loader also
checks `runtimes/win-x64/native`). `--native=<dir>` folders are searched first. See
`docs/NATIVE_DEPENDENCIES.md`. If no native folder exists the app creates an empty `native/` beside
the executable so the drop location is obvious.

## Notes

- The engine is created off the UI thread (native loading blocks); engine events are marshalled by
  the shared view model through the form's synchronization context (`WinFormsPlayerHost.cs`).
- Closing disposes the player off the UI thread, then closes the form.
