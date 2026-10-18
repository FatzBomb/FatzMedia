# Stage Update

Stage: 017A.5 - Each display its own cadence
Date: 2026-10-18

## Status: IMPLEMENTED - pending manual smoke

Owner request: "Continue 17a (4 and 5)".

Delivered: see the stage file, slice 5.

**Core:**
- `MediaCompositionTargetFilter`, and `MediaCompositionFrame.Targets` / `IsForTarget`.
- `VideoFrameEventArgs.Targets` (internal).
- `PresentationTimingGroup.Targets`.
- The coordinator groups surfaces by display (`TimingLayout`, rebuilt only when surfaces or
  their displays change). It republishes the surface snapshot on attach, resize and detach, so
  the timing source never takes the composition lock. A thread-static filter applies to every
  composition built while a targeted frame is presented.
- `VideoPipeline.DisplayLocked.cs`: per-display state (`DisplayLockedTarget`), earliest-refresh
  scheduling, and re-raising the current frame for a display that has not shown it yet.

**Renderers:** Direct3D11 (`D3D11MediaCompositionPresenter`) and software
(`SoftwareRendererBackend`) present a filtered composition to its targets only. A filter whose
surfaces just detached is a no-op, not a failure.

**Tests:**
- Coordinator grouping: two displays, a surface without timing, one display needs no filter,
  no primary timing means no groups.
- A targeted frame is composed for its targets only.
- The software backend presents to the subset.
- A two-display simulation: 25 fps keeps an even 2:2 on a 50 Hz projector while the 60 Hz primary
  keeps its 2/3 mix, from shared frames.

## Decisions made without the owner

- **Frames stay shared and only move forward.** Each display picks when it shows a frame, not a
  different frame stream; there is one decoder queue and one media clock.
  - A display whose refresh comes a little before another's may show a frame up to one work
    budget (a few ms) early, instead of keeping frames the other display already released.
- **CD+G, subtitles and backgrounds still update every target as they change.** Only video
  frames are paced per display, so overlays never wait for a display's refresh.
- **A surface with no timing follows the primary**, so it is never left without frames.

## Verification

- Linux:
  - full-solution Release build clean;
  - `dotnet format --verify-no-changes` clean;
  - Portable tests pass (864);
  - boundary scripts pass.
- Not runnable here: two real monitors. Covered by the manual smoke below.

## Manual smoke (Windows, Direct3D11, two monitors with different rates if available)

1. Open the Secondary Window (A/V flyout) and put it on the other monitor (for example 60 Hz
   and 50 Hz, or 60 and 144). Turn **Display-Locked** on and play a film.
   - The Log says "on: 2 displays (60 Hz, 50 Hz), each following its own refreshes".
2. Both windows play smoothly at their own rate. Every minute (and on pause) there is a
   statistics line per display, marked "(primary display)" and "(another display)".
3. Drag the secondary window onto the primary's monitor: the Log returns to one display, and
   playback continues without a hitch.
4. CD+G with a video background in the secondary window: lyrics update on both windows.
5. Close the secondary window during playback: the primary is unaffected.
