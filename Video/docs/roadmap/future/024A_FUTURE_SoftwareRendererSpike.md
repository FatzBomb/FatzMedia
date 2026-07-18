<!-- Conditional future stage. Promote only when its activation condition is met. -->

Stage file status: FUTURE.

# Future Stage 024A - Software Renderer Spike

## Status

Not active. The current request did not identify a concrete software-rendering
consumer, so no speculative renderer implementation was added.

## Activate Only For

- Deterministic image output used by automated visual-regression tests.
- Thumbnail generation or offline frame export.
- Headless composition in a server, worker, or test process.
- GPU diagnostic comparison that renders the same Core composition plan in software.

## Required Before Implementation

- One selected consumer with acceptance criteria and representative inputs.
- Required output pixel format, dimensions, color space, alpha behavior, and timing.
- Performance and determinism targets.
- Explicit decision whether text rasterization is shared or separately implemented.

## Must Not Become

- An automatic playback fallback without product requirements and performance testing.
- A second copy of Core composition policy; it must execute the existing Core plans.
- A reason to add platform/UI dependencies to Core.
