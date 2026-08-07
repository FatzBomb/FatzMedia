# Idea Stage 030A - Base-Visual Transform Pipeline

Status: Good idea. Recommended after Stages 029A-029C.

## Why This Is Worth Doing

Hosts want to adjust the presented media - tint for branding or mood lighting,
brightness/contrast for difficult projection environments, hue/saturation/lightness
for venue styling. Doing this once, in Core, with renderer parity gives every host the
same deterministic result on every renderer instead of per-renderer approximations.

## Recommended Direction

- Transform set: tint color with strength, hue, brightness, contrast, saturation, and
  an explicitly defined lightness (a defined lightness channel, not an underspecified
  brightness proxy).
- Core-owned and applied to the base visual (background video/image) by default; it
  must not apply to lyrics or subtitle overlays, which keep their own independent
  styling.
- Expressed as a Core composition-plan concept so every renderer implements the same
  transform, not its own interpretation.
- Neutral defaults: the transform pipeline is disabled unless configured; an unset
  transform is the identity, so existing output is byte-identical.
- Deterministic exact-pixel reference: the transform math extends the Stage 024A
  software renderer's conformance suite so every stage of every transform is covered
  by exact-pixel tests.
- Direct3D11 parity: the same equations run in the Direct3D11 passes, verified against
  the software reference.
- Define the exact math in the Core contract: per-channel equations, tint blend, the
  contrast pivot, hue rotation, saturation factor, the lightness definition, and a
  fixed operation order so both renderers always agree.

## Recommended Immediate Action

Write the single reference math spec (operation order, per-channel equations, tint
blend, lightness definition) and pin it with exact-pixel fixtures in the software
renderer before touching any Direct3D11 pass.

## Must Preserve

- Independent lyrics/subtitle styling; transforms never reach those overlays.
- Composition-plan output when transforms are unset (identity, byte-identical).
- Deterministic software-renderer output for the conformance suite.
- Direct3D11 behavior when the transform pipeline is disabled.

## Explicit Non-Goals

- Applying transforms to lyrics or subtitle layers in the first slice.
- Per-clip or animated transform keyframes in the first slice.
- Color management, gamut mapping, or HDR tone mapping.
- Reinterpreting the Core transform contract inside any renderer package.
