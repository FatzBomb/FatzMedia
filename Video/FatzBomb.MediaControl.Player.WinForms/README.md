# FatzBomb.MediaControl.Player.WinForms

Minimal WinForms test player for `FatzBomb.MediaControl`. Windows only (`net10.0-windows7.0`, x64).

## What it tests

- The `FatzBomb.MediaControl.WinForms` adapter: an HWND `MediaSurface` bound to a `MediaControlPlayer`
  (attach, resize, DPI, detach on close).
- The Windows + Direct3D11 platform (`WindowsDirect3D11MediaControlPlatformFactory`) in a non-WPF host.
- Open, Play/Pause, Stop, seek bar (seeks when the drag ends), volume, loop.
- The diagnostics list: every `MediaDiagnostic` and `MediaError` event, newest first (the list is
  capped at 500 entries). Missing native libraries are listed at startup.

It has no renderer choice and no effects beyond volume; use the WPF player for those.

## Run

```powershell
dotnet run --project FatzBomb.MediaControl.Player.WinForms -c Release -- [media-file]
```

## Native libraries

The packages ship no BASS or FFmpeg binaries. Put the x64 DLLs (BASS, BASS_FX, FFmpeg 9.x) in

```text
FatzBomb.MediaControl.Player.WinForms/native/
```

They are copied next to the app at build/publish time (`native/` under the app folder; the loader also
checks `runtimes/win-x64/native`). See `docs/NATIVE_DEPENDENCIES.md`. If no native folder exists the app
creates an empty `native/` beside the executable so the drop location is obvious.

## Notes

- The engine is created off the UI thread (native loading blocks); engine events are marshalled with
  `BeginInvoke`.
- Closing disposes the player off the UI thread, then closes the form.
