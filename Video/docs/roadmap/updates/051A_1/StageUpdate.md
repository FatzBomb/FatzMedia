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
| WPF test player, A/B demo | `--ab-demo=` window (`AbSwapDemoWindow`; options and summary in Player.Shared `AbSwapDemo.cs`, tested): two players, swaps on `FirstFrameReady`, a `[ABDemo]` log line per swap. |
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
- The whole solution builds in Release (warnings as errors) with `EnableWindowsTargeting=true`, 0 warnings,
  against FatzAudio rc.1 packed locally from its source (managed part only, commit 59741a5).
- Linux.Tests: 203 passed with FFmpeg; the 9 `FatzAudioEngineNativeTests` fail only because the local
  package has no native `libfatzaudio.so` (`DllNotFoundException`), unrelated. New
  `LinuxFirstFrameNativeTests` (real FFmpeg and software renderer): a load before the surface attaches
  still reaches the surface and arms the first frame (why the startup delay could go); an A/B swap of
  two players reports each load's first frame after a frame reached its surface (76-256 ms per swap).
- New Windows-only tests (CI runs them): `WpfFirstFrameReadyTests` (composited surface in a shown window:
  once per load, the bitmap written first, the counter) and a WinForms frame-sink test.
- Not verified: anything on screen (same-frame swap, native timing, the counter's numbers).

## Manual smoke (Windows, PowerShell)

`scripts/smoke-051A.ps1` runs all of it: it fetches and switches to the branch (it stops if you have
uncommitted changes), builds, asks you to pick a 1080p60 video and a song from `C:\media`, runs each
test, reads the players' logs, and writes PASS/FAIL to a Desktop folder (`FatzBomb-051A-<date>`). The
results are copied to the clipboard at the end. Needs `FATZAUDIO_PACKAGES_TOKEN` set (as for every build)
and the BASS and FFmpeg DLLs in the players' `native\` folders.

Desktop (all tests):

```powershell
cd C:\development\nuget\FatzbombMedia
```

```powershell
powershell -ExecutionPolicy Bypass -File scripts\smoke-051A.ps1
```

Laptop (only the UI-thread numbers, test 3):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\smoke-051A.ps1 -Steps 3
```

| Test | Kind | What it checks |
| --- | --- | --- |
| 1 | automatic | WPF native and composited: the startup video gives exactly one `first frame ready` line naming its path; prints the seconds from window to first frame. |
| 2 | guided (you drop 3 files) | Two video loads, with pause, seek and an MP3 between them, give exactly two lines and none for the MP3. |
| 3 | automatic + 2 drops | Composited UI-thread time, ms/s and ms/frame: one player at window size, the same maximized, then three players. Records machine, GPU and power state. These are 047A's before numbers. |
| 4 | automatic | `--no-auto-hide`, native and composited: one line each. |
| 5 | automatic + you watch | The WPF player's A/B swap demo (`--ab-demo`): 20 composited swaps, 6 with the waiting surface `Collapsed` (counter-check), 10 native. The log must show every swap's first frame, no misses and no warnings; you answer whether there was a black flash, an old frame, or the bar changing apart from the clip. Native-mode offset is noted, not failed. |
| 6 | automatic + 1 question | Avalonia and WinForms players load the startup video at once. |

Media is offered from `C:\media` and `D:\- Testing Files` (two levels deep, so `sample files` too);
pass `-MediaDir` for other folders. TriviaBomb itself is a final check after this.
