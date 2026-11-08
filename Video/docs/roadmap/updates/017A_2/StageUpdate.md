# Stage Update

Stage: 017A.2 - Cadence report
Date: 2026-10-17

## Status: IMPLEMENTED - owner smoke passed 2026-10-18

Delivered:

- `Core/Graphics/Cadence/CadenceAdvice`: turns the video frame rate and the display refresh
  rate into one readable line. Rates snap to exact fractions (23.976 = 24000/1001) within
  0.01 Hz, and the exact ratio display / source (what the 017A cadence model converges to)
  decides between:
  - smooth: every frame held for the same number of refreshes (25 fps on 50 Hz, 24 on 120);
  - 3:2 pulldown (24 or 23.976 fps on 60 Hz);
  - an occasional long or short hold, with how often (29.97 fps on 60 Hz: about every 16.7 s);
  - an uneven mix (25 fps on 60 Hz);
  - skipped frames when the display is slower than the video (60 fps on 50 Hz: about 17%;
    60 fps on 59.94 Hz: one about every 16.7 s).
  When not smooth, it lists the common refresh rates that would be.
- `VideoPipeline` raises it as an Info diagnostic (`VideoCadence`) once after each video
  opens. It says whether the display rate came from the platform (which falls back to 60 Hz)
  or from the host's `SetDisplayRefreshRate`.
- Tests: `CadenceAdviceTests`.

No presentation timing changed: this slice only reports.

## Decisions made without the owner

- Report before changing frame selection. Picking frames at present time (next slice) changes
  timing behaviour and needs on-display validation; the report shows which files and displays
  would benefit first.
- The detected rate is still DWM's global composition rate on Windows. On a mixed-refresh
  multi-monitor setup it can describe a different monitor; the report says "detected" so this
  is visible. A per-monitor rate for the window's own monitor is the next renderer-side step.

## Manual smoke (Windows, native DLLs)

1. Open a 23.976 or 24 fps film on a 60 Hz display: the Log panel shows a `VideoCadence`
   line saying 3:2 pulldown and listing smooth rates (24, 48, 72, 120, 144, 240 Hz or the
   NTSC ones).
2. Open a 25 fps or 50 fps video: the line says "between 2 and 3 refreshes" or similar.
3. If your display can switch to 120 Hz or 24 Hz, switch, reopen the 24 fps film: the line
   says each frame is shown for 5 (or 1) refreshes, smooth.
4. Open an audio file: no `VideoCadence` line.

## Review follow-up (same day, local code review)

- Near-matches no longer read "about 0% of frames are never shown" or "smooth": the report
  works from the exact rate ratio instead of a one-minute simulation, so slow drift (60 fps
  on 60.015 Hz, one long hold about every 67 s) and rare skips are described with how often
  they happen.
- "1 refreshes" reads "one refresh"; frame rates above 300 fps (container timebases such as
  1000) are not reported; `FrameRate.ToString` is culture-invariant and reused.
- The re-report on `SetDisplayRefreshRate` was removed: it read pipeline state from the host
  thread without the transport gate. The report is raised once per open.
- The label says the platform rate may be a 60 Hz fallback.
- Not changed: CD+G's no-op settle in the shared seek pump (ENG-013) stays a host callback
  rather than a pump option; the CD+G close join is 2 s (was 1 s); `CdgPipelineSeekTests`
  waits a fixed 250 ms to check for a second render.
