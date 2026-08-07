# Idea Stage 024A - Deterministic Software Renderer

Status: Good idea. Recommended next.

## Why This Is Worth Doing

A deterministic software renderer turns Core composition plans into in-memory pixels
without a GPU or live window. It enables automated visual-regression tests, thumbnails,
offline frame export, headless composition, and direct comparison with GPU output.

## Recommended Direction

- Add a portable `FatzBomb.MediaControl.Rendering.Software` project.
- Consume existing `MediaCompositionFrame` and layer intent rather than duplicating policy.
- Start with clear color, software BGRA background, stretch, wash, CDG, and pre-rasterized subtitles.
- Define pixel format, color space, alpha behavior, dimensions, and deterministic rounding explicitly.
- Verify output with exact-pixel fixtures or stable golden-image hashes.

## Recommended Immediate Action

Promote 024A and define the first output contract: one fixed-size in-memory frame,
software-backed composition inputs only, deterministic BGRA output, and exact tests for
clear, stretch, wash alpha, layer ordering, CDG palette, and subtitle placement.

## Must Preserve

- Core ownership of composition and layer policy.
- Renderer independence from UI frameworks and OS platform packages.
- Existing Direct3D11 behavior and package selection.
- Borrowed-resource and retained-frame ownership rules.

## Explicit Non-Goals

- Automatic playback fallback in the first stage.
- Native GPU texture or hardware-decoder interop.
- A new text-layout or text-rasterization engine.
- Window, swap-chain, audio, timing, or host UI integration.
- A second copy of Core composition policy.
