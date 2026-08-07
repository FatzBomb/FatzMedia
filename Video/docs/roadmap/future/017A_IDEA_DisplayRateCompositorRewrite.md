# Idea Stage 017A - Display-Rate Compositor and Presentation Scheduling

Status: Good idea.

## Why This Is Worth Doing

Display-rate scheduling can provide smoother 23.976/24fps presentation on fixed-refresh
displays, make frame-repeat decisions explicit, and give multi-target presentation a
clear cadence model instead of relying only on source-frame timing.

## Recommended Direction

- Define a renderer-neutral display cadence and repeat/drop model in Core.
- Let each renderer expose the timing signals it can support without owning playback policy.
- Model mixed source/display rates and independently updating CDG/subtitle layers.
- Introduce scheduling changes behind deterministic cadence tests before native integration.

## Recommended Immediate Action

Create a cadence simulator for representative 23.976, 24, 25, 29.97, 30, and 60fps
sources on 50, 59.94, 60, and 120Hz displays. Use it to specify repeat, drop, drift, and
multi-target behavior.

## Must Preserve

- Stable source-cadence playback as a fallback.
- Independent CDG and subtitle updates.
- Primary/secondary target parity.
- Hardware and software video paths.
- Audio-clock authority and A/V synchronization.

## Explicit Non-Goals

- Rewriting decoders, demuxers, or audio clocks.
- Hard-coding DWM, DXGI, or one renderer's vblank model into Core.
- Assuming every target has the same refresh rate.
- Combining display scheduling with unrelated compositor visual changes.
