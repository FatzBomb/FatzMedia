# Stage Update

Stage: 017A.2 - Cadence report
Date: 2026-10-17

## Status: IMPLEMENTED - manual Player smoke pending

Delivered:

- `Core/Graphics/Cadence/CadenceAdvice`: turns the video frame rate and the display refresh
  rate into one readable line. Rates snap to exact fractions (23.976 = 24000/1001) within
  0.01 Hz, and one minute of the 017A cadence model decides between:
  - smooth: every frame held for the same number of refreshes (25 fps on 50 Hz, 24 on 120);
  - 3:2 pulldown (24 or 23.976 fps on 60 Hz);
  - an occasional long or short hold, with how often (29.97 fps on 60 Hz: about every 16.7 s);
  - an uneven mix (25 fps on 60 Hz);
  - skipped frames when the display is slower than the video (60 fps on 50 Hz: about 17%).
  When not smooth, it lists the common refresh rates that would be.
- `VideoPipeline` raises it as an Info diagnostic (`VideoCadence`) after each video opens,
  and again when the host changes the refresh rate with `SetDisplayRefreshRate`. It says
  whether the rate was detected or set by the host.
- Tests: `CadenceAdviceTests` (21 cases).

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
