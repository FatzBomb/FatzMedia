# Stage Update

Stage: 017A.5 - Each display its own cadence
Date: 2026-10-18

## Status: IMPLEMENTED - owner smoke passed 2026-10-18 (smoke 4: near-equal to source cadence)

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

## Owner smoke 2 (2026-10-18): stutters with two windows; none with display-locked off

Both runs had the secondary window on the second monitor (59.96 and 60.00 Hz). The new counters
ruled out three causes, all at or near zero:
- source-cadence passes 0;
- vertical-blank re-measurements 0;
- undecoded-frame waits 3 to 5.

Each display missed about 170 refreshes a minute, in about 80 gaps (longest 333 and 567 ms),
with almost no slow passes.

Cause: slice 5 served one display per pass, whichever refresh came first. Two monitors at almost
the same rate drift in and out of step (every ~24 s here). While their refreshes fell within
one pass's work of each other, serving one made the other miss its refresh, and the next pass
did the same. So one display kept losing turns for up to half a second, on a regular cycle.

Fixed: a pass serves every display whose own wake-up falls before the pass would be done.
- Each display still works out its own refresh's media time, phase lock and statistics.
- The frame for the earliest refresh is presented once, to all of their surfaces.
- A display that has not shown the frame another display moved on to gets it in the same pass.

Test: a 60 s schedule simulation at 59.96 and 60.00 Hz with 3 ms passes misses more than 100
refreshes serving one display per pass, and none with the fix.

## Owner smoke 3 (2026-10-18): close; small jumps on slow pans with display-locked on

Owner: single window with it on showed small jitters, "not nearly as bad as previously", a
little more noticeable than off. Two windows were the same or better. Off: no noticeable issues.

Log, single window, one minute:
- 1434 frames on 3592 refreshes; about 1438 and 3598 is perfect.
- Refreshes per frame 2: 714, 3: 715.
- 8 missed refreshes; no fallbacks, re-measurements or decode waits.
- 52 presents finished after their refresh.

The Perf lines show the difference between the paths:
- Source cadence presents about 10 ms ahead of a frame's time (`maxEarly` 10.3 ms).
- Display-locked woke only its measured budget ahead, 2 to 3 ms (work is about 1 ms).

Windows' compositor takes a window's frame a little before the refresh, so presents that late
missed the cut-off about once a second and showed a refresh later: the small jumps.

Fixed: the wake-up lead is at least half a refresh (8.3 ms at 60 Hz), within the existing 2 to
12 ms and 60 % caps. Selection still uses the refresh's own media time, and frames are decoded
well ahead, so only the present moves earlier.

## Owner smoke 4 (2026-10-18): passed, near-equal to display-locked off

Owner: "I won't say perfect, but my eyes can play tricks staring so long. So close at least, if
not equal."

Log, two one-minute reports (24 fps on 60 Hz, perfect is about 1438 frames on 3598 refreshes):

| Report | Frames / refreshes | Held 2 / 3 | Late presents | Missed refreshes | Fallbacks, re-measurements |
| --- | --- | --- | --- | --- | --- |
| 1 | 1435 / 3596 | 716 / 720 | 6 | 4 | 0 |
| 2 | 1438 / 3600 | - | 1 | 0 | 0 |

Late presents fell from 52 a minute (smoke 3) to 1 to 6, so the half-refresh lead fixed the
missed compositor cut-off. The 2:3 mix is even.

Keep or remove: kept as an opt-in Player option, off by default (owner: "it could be needed for
some people"). Source cadence stays the default; display-locked is for setups where the monitor's own
blank timing gives a better cadence (displays at different rates, rates that drift from nominal).

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
