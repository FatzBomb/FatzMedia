# 051A slice 1 - First-frame-ready signal, composited UI-thread counter, no startup delay

Date: 2026-11-06. Status: implemented, pending manual smoke (Windows).
Stage: `active/051A_ACTIVE_FirstFrameReadySignal.md` (investigation, proposal, acceptance).

## What changed

| Area | Change |
| --- | --- |
| Core `MediaSurfaceContentTracker` | `FirstFrameLoad`: a load number per load with visual content (and per session already open when set), reported once a frame of it was presented (the latch counts); cleared by close, error, session change; `Changed` raised when it is set, `AutoHide` on or off. |
| WPF `MediaSurface` | `FirstFrameReady` (`MediaSurface.FirstFrame.cs`); composited: a waiting frame is written inline (`WpfCompositedSurfaceHost.PublishPendingFrame`, now public to the adapter, returns whether it wrote) and the event follows in the same dispatcher operation, after the host is visible; also checked after each scheduled publish. Native: after the child HWND is shown. `CompositedUiThreadTime`, `CompositedFramesPublished` (totals kept across host re-creation). |
| Avalonia `MediaSurface` | `FirstFrameReady`; frame sink: `AvaloniaFrameSink.PublishPending` (inline publish) then invalidate; GPU shared image: after the content is shown (best effort). |
| WinForms `MediaSurface` | `FirstFrameReady`; frame sink: inline publish and paint; native window: after the child window is shown. |
| `PlayerViewModel` | `StartupMediaDelay` (500 ms) and its cancellation source removed: the startup media loads as soon as the engine is ready. |
| Player.Shared | `UiThreadCostMeter`, `PlayerStatsLine.WithUiThreadCost`. |
| WPF test player | Stats line: `surface WxH via frame sink, UI thread x.x ms/s (y.yy ms/frame)` (primary and second screen); log line `P1 first frame ready: <file> via <path>` per load. |

## Decisions

- Name `FirstFrameReady`: the frame is on the surface and its host is shown; the host's own element
  visibility is the host's business (the B surface of an A/B pair is hidden by the host while it waits).
- Hide a waiting surface with WPF `Visibility.Hidden` / Avalonia `Opacity = 0` / WinForms
  `Visible = false`, never `Collapsed`/`IsVisible = false` (no layout size: the frame would be rendered at 1x1).
- One event per load, no load id in the args (Core has none); subscribe before `LoadAsync`.
- The startup delay was not needed: the binding is synchronous, attach re-presents the current frame,
  and the device diagnostic is subscribed before `Player` is set (Avalonia test
  `ALoadBeforeTheSurfaceAttaches_StillRaisesWhenItAttaches`).
- Counter is WPF only (047A is WPF); it measures the bitmap write on the UI thread, not WPF's render thread.
- Found, not fixed: ENG-058 (latch and a background frame), ENG-059 (WPF composited keeps its last
  bitmap while auto-hidden).

## Verified in the Linux sandbox

- Portable.Tests 1784 passed (5 tracker tests, 1 meter test, the startup test replaced).
- Avalonia.Tests 52 passed (5 new `FirstFrameReadyTests`: once per load with the frame in the bitmap,
  AutoHide on and off, a new load raises again, audio-only never, an announced frame waits for the
  bitmap, a load before the surface attaches).
- WPF and WinForms adapters build with `EnableWindowsTargeting=true`, 0 warnings; `dotnet format
  --verify-no-changes` clean on every touched project; the four boundary scripts pass.
- **Not built here:** the WPF, Avalonia and WinForms test players and Linux.Tests (they restore the
  token-gated FatzAudio package; this session could not build it from source). Windows CI builds them.
- Not verified: anything on screen (same-frame swap, native timing, the counter's numbers).

## Manual smoke (Windows, PowerShell)

Repo at `C:\development\nuget\FatzbombMedia`, BASS and FFmpeg DLLs in
`FatzBomb.MediaControl.Player\native\`, media in `C:\media`.

```powershell
cd C:\development\nuget\FatzbombMedia
git fetch origin; git checkout claude/zen-tesla-ux51s6; git pull
dotnet build FatzBomb.MediaControl.sln -c Release
$log = Join-Path $env:TEMP 'FatzBombMedia\player-debug.log'
function Run-Player([string[]]$PlayerArgs) {
  Remove-Item $log -ErrorAction SilentlyContinue
  dotnet run --project FatzBomb.MediaControl.Player -c Release --no-build -- @PlayerArgs
}
```

1. **No startup delay.** `Run-Player @('C:\media\video.mp4')`: the video shows at once (no half-second
   black). Close, then:
   `Select-String $log -Pattern 'first frame ready','Loaded:' | Select-Object -First 4`
   expect one `P1 first frame ready: video.mp4 via native window` after `Loaded: C:\media\video.mp4`.
2. **Once per load.** Start `Run-Player @()`, drop `C:\media\video.mp4`, pause, seek, play, loop it
   (A/V flyout), then drop `C:\media\song.mp3`, then `C:\media\video.mp4` again. Close, then
   `(Select-String $log -Pattern 'first frame ready').Count` expect **2** (the two video loads; none
   for the MP3, none for pause/seek/loop).
3. **Composited and the counter (047A's before numbers).** `Run-Player @('--surface=framesink','--players=3','C:\media\video.mp4')`,
   drop a 1080p60 clip on P2 and P3, let it run 30 s, close, then
   `Select-String $log -Pattern '\[Stats\].*UI thread' | Select-Object -Last 6`
   expect `surface WxH via frame sink, UI thread x.x ms/s (y.yy ms/frame)` for each player. Note the
   ms/frame at window size and maximized (one player, F11), on the desktop and on the laptop: these are
   047A's before numbers. Also `Select-String $log -Pattern 'first frame ready'` shows `via frame sink`.
4. **Auto-hide off.** `Run-Player @('--no-auto-hide','--surface=framesink','C:\media\video.mp4')`: one
   `first frame ready` line, as in step 1.
5. **A/B swap, same frame (TriviaBomb, WPF composited).** In TriviaBomb (or a small WPF app following
   the WPF README's A/B example): two `WpfComposited` surfaces stacked, B `Visibility.Hidden`; swap clips
   from the handler 20 times, with a question panel shown in the same handler. Watch for a black frame,
   the old clip's last frame on the new surface, or the panel appearing a frame before/after the new
   clip (a 240 fps phone video helps). Repeat once with B `Collapsed`: the first frame looks blurred
   or wrong-sized for a frame (why the pattern says Hidden).
6. **Native mode (best effort).** Same app in `NativeHwndOverlay`: the event fires per swap; note if the
   panel leads or lags the video by a frame (expected possible; record it).
7. **Avalonia and WinForms.** `dotnet run --project FatzBomb.MediaControl.Player.Avalonia -c Release -f net10.0-windows7.0 -- C:\media\video.mp4`
   and `dotnet run --project FatzBomb.MediaControl.Player.WinForms -c Release -- C:\media\video.mp4`: the
   video shows at once (no startup delay). (The event is covered by the Avalonia headless tests; these
   players do not log it.)
