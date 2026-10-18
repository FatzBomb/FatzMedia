# Stage Update

Stage: 017A.4 - Display-locked frame selection
Date: 2026-10-18

## Status: IMPLEMENTED - pending manual smoke

Owner request: "Continue 17a (4 and 5)".

Delivered: see the stage file, slice 4.

**Core:**
- `Api/VideoFrameTiming.cs`.
- `Core/Graphics/Composition/PresentationVsyncTiming.cs`.
- `IMediaRendererBackend.TryGetSurfaceVsyncTiming` (default false).
- `Core/Graphics/Cadence/VsyncMediaClock.cs`, `DisplayLockedTiming.cs`, `CadencePhaseLock.cs`.
- `Pipeline/IPresentationTimingSink.cs`.
- `Pipeline/VideoPipeline.DisplayLocked.cs`, plus loop hooks in `VideoPipeline.cs`.
- The coordinator hands the pipeline the primary display's timing.
- `MediaControlPlayer.VideoFrameTiming` reaches the pipeline through `PipelineMediaEngine` and
  `PlaybackCoordinator`, and is re-applied after `Reset`.

**Direct3D11:**
- `MonitorVblankClocks.cs`, `VblankGrid.cs`.
- `NativeD3D11PresentationBridge.TryGetSurfaceVsyncTiming` for native-window surfaces
  (DirectComposition and HWND).

**Player:** **Display-Locked** check box (A/V flyout).

**Tests:**
- `DisplayLockedTimingTests.cs` (23 cases, shared folder, so they also run in Portable).
- `D3D11VblankGridTests.cs` (5, Windows-only; also checked on Linux in a throwaway project).
- The cadence simulation feeds a refresh grid and an audio clock with +-2 ms jitter through the
  filter, the phase lock and the selection:
  - 23.976 fps on 59.94 Hz stays exact 3:2 at boundary phases of 1.5 and 4 ms;
  - 24 on 60 is clean;
  - 23.976 on 60 Hz (0.1 % drift) breaks the cadence 15 times in 120 s, against 14 for the exact
    model and 198 straight from the raw clock;
  - 25 on 60 keeps the model's 2/3 mix.

## Decisions made without the owner

- **Opt-in, off by default.** It changes when every video frame is presented, and that needs
  your eyes on real displays. The source-cadence path is untouched.
- **Vertical blanks from the monitor (`WaitForVBlank`), not swap-chain statistics.** DXGI frame
  statistics are unreliable for the DirectComposition swap chain the Player uses. DWM timing is
  one desktop-wide rate, the problem slice 3 solved. The monitor's own blanks are right for
  every swap chain and window.
  - Cost: one waiting thread per monitor in use, and only while display-locked video plays.
- **Half-refresh phase.** Frames start on the refresh nearest their timestamp, so on average
  they are neither early nor late (strict "latest reached" shows them half a refresh late). This
  is the model's phase term: it never changes how many refreshes a frame is held.
  - Display latency after the present (DWM composition, usually a refresh) is constant, and the
    Sync flyout's A/V offset still adjusts it.
- **Hysteresis only for simple ratios** (up to 4 phase families). Uneven ratios (25 fps on
  60 Hz) have no boundary to protect.
  - The phase lock moves A/V timing by at most a quarter refresh for 2.5 refreshes per frame,
    and half a refresh for whole ratios.
- **Each pass decides on its own.** A pass without current timing uses the source-cadence path.
  The thread starting up, a monitor change or a sleeping display never stops video.

## Verification

- Linux:
  - full-solution Release build clean (warnings as errors);
  - `dotnet format --verify-no-changes` clean;
  - Portable tests pass (855);
  - boundary scripts pass.
- Not runnable here: `WaitForVBlank`, DirectComposition presents and real displays. Covered by
  the manual smoke below.

## Manual smoke (Windows, native DLLs, Direct3D11 renderer)

1. Play a 23.976 or 24 fps film with **Display-Locked** off, then on (A/V flyout). With it on,
   within a second the Log shows "Display-locked video timing on: frames follow the 59.94 Hz
   refreshes of the video's display" (your rate).
   - Watch a slow pan: motion should be even, a steady 3:2.
2. After a minute, or when you pause, the Log has a "Display-locked timing over ..." line:
   - refreshes per frame mostly 2 and 3, about equal counts, for 24 fps on 60 Hz;
   - at 120/144 Hz the counts differ by rate;
   - few presents finished after their refresh.
3. Seek, loop, pause and resume with it on: the picture keeps up, audio stays in sync, and the
   clock restarts are counted.
4. Run `--renderer=software`, or the WPF-composited MODE, with it on: the Log says "unavailable"
   and playback is as before.
5. A/V sync: with it on, compare lip sync against off. If it moved, the A/V offset in the Sync
   flyout corrects it; report how much.
6. Optional: a capture-script run with it on and off, as for ENG-018.
