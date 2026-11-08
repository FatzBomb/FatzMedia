# Completed Stage 030A - Base-Visual Transform Pipeline

Prepared: 2026-08-08
Completed: 2026-08-17
Updated: 2026-08-17
Status: Complete

**Implemented 2026-08-09** (pipeline, math, renderer parity); **Player advanced validation
controls added 2026-08-10** (live validation controls in the host).
**Contract revised 2026-08-11** — the fixed operation order changed to
hue → saturation → TV-style contrast gain → brightness offset → per-channel RGB
gains (contrast multiplies from black instead of pivoting around 0.5, and runs
before the brightness offset); a new `IncludeOverlays` member adds a
whole-composition mode (the transform moves off Layer 0 to a final postprocess
pass over the completed composition); the Player host UI was reworked to a
persistent bottom chrome plus one-at-a-time Sync/Video/Audio/A-V flyouts; the
MIDI lyrics checkbox was removed and `ShowMidiLyrics = true` is always set by
the host. Concise contract history: the former tint members (`TintArgb` /
`TintStrength`) were replaced by a color-wash model (`ColorWashArgb` /
`ColorWashStrength`), which was in turn replaced by independent per-channel RGB
gains (`RedGain` / `GreenGain` / `BlueGain`); the HSL `Lightness` member and its
math stage were removed, and the color-wash preset type/file
(`ColorWashPreset.cs`) was removed with no replacement. The pinned decisions and
Core Math sections below are now the exact implemented reference, not a plan.

**Stage 030A fixes 2026-08-11 (second pass)** — a DirectComposition split-path
bug was found and fixed: with `IncludeOverlays = false`, the cached split base
state for static images/backgrounds omitted `BaseVisualTransform`, so transform
changes were skipped for static images while per-frame video revisions masked
the defect. `DirectCompositionOverlaySplitBaseState` now includes
`BaseVisualTransform` (so any transform change invalidates the cached split
state and forces a re-present), pinned by the regression test
`D3D11RendererPresentationServicesTests.DirectCompositionBaseStateChangesWhenStaticImageTransformChanges`.
The Loop control also moved from the A/V flyout to the persistent transport
strip as a stateful icon button with accent highlight; the duplicate A/V Loop
checkbox was removed.

Automated validation: Release 0 warnings/errors; **438 tests**;
format/Core/WPF/parity gates green; Core, Software, and Direct3D11 nupkg +
snupkg creation passed (Direct3D11 pack re-verified for the split-state fix);
focused final review **PASS**. The manual static-image Include Overlays
off/on, loop icon, and both-renderer visual smoke passed on 2026-08-17,
closing the final gate.

## Objective

Hosts want to adjust the presented media — per-channel RGB gain for channel-level
color correction (removing a channel, boosting a channel, or tinting toward
red/cyan/aqua), brightness/contrast for difficult projection environments,
hue/saturation for venue styling. Doing this once, in Core, with renderer
parity gives every host the same deterministic result on every renderer instead
of per-renderer approximations. This stage adds a single immutable
`BaseVisualTransform` model on the public session. By default it applies only to
the Layer 0 base visual (main video/image and background video/image); with
`IncludeOverlays = true` and a non-identity transform, the same math is applied
to the completed composition as a whole (wash, CDG, subtitles, and other
overlays included) through a final postprocess pass. Both modes have an
exact-pixel software reference and a Direct3D11 shader parity pass.

## Implementation Status (2026-08-09; Player controls 2026-08-10; contract revised 2026-08-11)

Phases 1-4 and the automated portion of Phase 5 are implemented. The pinned decisions and Core Math sections
below are now the exact implemented reference, not a plan. What exists:

- **Public model** — `FatzBomb.MediaControl/Api/BaseVisualTransform.cs`
  (readonly record struct, namespace `FatzBomb.MediaControl`). Init accessors
  clamp to the pinned ranges and treat NaN as the identity default, so
  `default`/`new()` is always safe: `RedGain`/`GreenGain`/`BlueGain` `1`,
  `HueDegrees`/`Brightness` `0`, `Contrast`/`Saturation` `1`, `IncludeOverlays`
  `false`. There is **no `Lightness` member**, no tint member, and no color-wash
  member. `IsIdentity` reflects only the six color members (a transform with
  `IncludeOverlays = true` but all-neutral color math is still identity and
  byte-identical); `Identity` surfaces the default.
- **Session surface** — `IMediaControlSession.BaseVisualTransform` +
  `BaseVisualTransformChanged` (change-only, previous/current payload),
  implemented in `MediaControlPlayer.VisualTransforms.cs` (partial) via
  `HostEventDispatcher`; the setter delegates to
  `MediaPresentationCoordinator.BaseVisualTransform`.
- **Core reference math** — `Core/Graphics/Composition/BaseVisualTransformMath.cs`
  (internal): identity fast path; unpremultiply → fixed-order stages (YIQ hue →
  Rec.709 saturation → TV-style contrast gain → additive brightness offset →
  independent per-channel RGB gain multiplication) with clamp-after-each-stage →
  re-premultiply → away-from-zero BGRA8 quantization.
- **Layer 0 carry only** — `MediaCompositionFrame.BaseVisualTransform` /
  `MediaCompositionLayer.BaseVisualTransform`, applied to the Background
  (Layer 0) descriptor only; all other layers stay identity
  (`MediaCompositionFrameTests.BackgroundLayerCarriesOnlyBaseVisualTransform`).
  The whole-composition mode is decided per frame from `IncludeOverlays`, not
  from the layer descriptors.
- **Software exact path** — `Rendering.Software/SoftwareBaseVisualTransform.cs`
  (internal) mirrors the reference math; `SoftwareCompositionRenderer` engages
  the per-layer path only when non-identity (identity output byte-identical).
  When `IncludeOverlays` is true and the transform is non-identity, the renderer
  skips the per-layer transform entirely and instead postprocesses the completed
  **premultiplied final buffer** (`ApplyFinalTransform`) after wash, CDG, and
  subtitle draws — the transform is moved, not double-applied.
- **Cross-reference conformance** — `BaseVisualTransformTests.SoftwareMathMatchesCoreReference`
  checks the software path against the Core reference (5 transforms × 3 pixels);
  `SoftwarePremultipliedMathMatchesCoreReference` covers the final-buffer
  postprocess path; `SoftwareCompositionRendererTests` pins exact-pixel fixtures
  per stage, in combination, and for the whole-composition mode.
- **TransformChanged render-without-upload** — new
  `MediaCompositionLayerDirtyFlags.TransformChanged`; the D3D11 layer-state diff
  raises `TransformChanged | RenderRequired` with no `UploadRequired`
  (`D3D11CompositionLayerStateTests.TransformChangeRequiresRenderWithoutUpload`).
- **Coordinator refresh + hardware routing** — the coordinator re-presents the
  current composition on transform change (`PresentCurrentComposition("base
  visual transform changed")`); a non-identity transform forces the hardware
  primary through `RenderHardwareVideoCompositionFrame` instead of direct
  zero-copy `PresentNativeFrame` (`MediaPresentationCoordinator.cs:644`).
- **Dedicated D3D11 shaders** — `PSBaseVisual` (exact equations, extended
  constant buffer `TransformAdjustments`/`TransformBlend`/`TransformRgbGains`)
  bound only when the Layer 0 transform is non-identity in per-layer mode, and
  `PSBaseVisualPremultiplied` for the whole-composition final postprocess;
  `PSBackground`/`_backgroundPixelShader` stays for identity backgrounds and the
  subtitle layer. Compile covered by
  `D3D11CompositionLayerStateTests.BaseVisualPixelShaderCompiles` and
  `BaseVisualPremultipliedPixelShaderCompiles`.
- **Whole-composition postprocess (D3D11)** — when `IncludeOverlays` is true and
  the transform is non-identity, the presenter composites the full frame
  (background drawn with identity) into a per-target cached offscreen
  B8G8R8A8 render target, then runs `PSBaseVisualPremultiplied` as an overwrite
  pass onto the final target. The offscreen target is keyed by target ID,
  recreated on size change, and disposed on reset; the DirectComposition overlay
  split is disabled in this mode (`D3D11RendererPresentationServices`).
- **Split-path base state carries the transform (root-cause fix)** — the cached
  `DirectCompositionOverlaySplitBaseState` used by the DirectComposition overlay
  split path (`IncludeOverlays = false`) omitted `BaseVisualTransform`, so a
  transform change was skipped for static images/backgrounds (the split base is
  re-presented only when its cached state changes; video revisions changed every
  frame and masked the bug). The record struct and
  `CreateDirectCompositionOverlaySplitBaseState` now include
  `frame.BaseVisualTransform`, so any transform change invalidates the cached
  base state and forces a re-present. Pinned by
  `D3D11RendererPresentationServicesTests.DirectCompositionBaseStateChangesWhenStaticImageTransformChanges`
  (two base states built from the same frame/target differ when only the
  transform changes).
- **Player host UI (2026-08-10/11)** — persistent bottom chrome (timeline,
  transport, fullscreen, volume, status) plus one-at-a-time Sync/Video/Audio/A-V
  flyout buttons; flyout content lives in
  `Controls/PlayerFlyoutPanel.xaml`; the active button highlights. The Video
  flyout carries HUE/SATURATION/BRIGHTNESS/CONTRAST + RED/GREEN/BLUE gain
  sliders, an **Include Overlays** checkbox, and Reset Visual; the Audio flyout
  carries pitch/tempo/normalize and the load-reset toggles; the A/V flyout
  carries autoplay/auto-hide/secondary-window/display/presentation-mode/background-opacity.
  Loop moved (2026-08-11) from the A/V flyout to the persistent transport strip
  as a stateful icon button (`ToggleChromeButtonStyle`, accent highlight when
  `LoopPlayback` is on); the duplicate A/V Loop checkbox was removed.
  The MIDI lyrics checkbox was removed — the host always sets
  `ShowMidiLyrics = true`. See "Player Host UI" below.

Validation status: Release 0 warnings/errors; **438 tests**;
format/Core/WPF/parity gates green; Core, Software, and Direct3D11 nupkg +
snupkg creation passed (Direct3D11 pack re-verified for the split-state fix);
focused final review PASS. The manual static-image Include Overlays
off/on, loop icon, and both-renderer flyout/final-composite visual smoke
passed on 2026-08-17.

## Scope (exact)

- One immutable Core model: `readonly record struct BaseVisualTransform` with
  identity defaults; a public session property plus a change event; Core-owned
  composition-plan state that carries the transform to renderers.
- **Per-layer mode (default, `IncludeOverlays = false`)** — applies **only** to
  the Layer 0 base visual — the composition `Background` layer (main video/image
  and background video/image). Wash, CDG, subtitle, karaoke text, and other
  overlays are never transformed; zero extra composition pass is performed.
- **Whole-composition mode (`IncludeOverlays = true` + non-identity)** — the
  transform applies to the completed composition as a whole, including wash, CDG
  overlay, subtitle overlay, and any other overlays, via a final postprocess
  pass. The transform is **moved** from Layer 0 to the final pass — never
  double-applied. `IncludeOverlays = true` with all-neutral color math stays
  byte-identical (identity bypass).
- Software renderer implements the exact pixel math against a deterministic
  reference; Direct3D11 implements the same equations in a dedicated base-visual
  pass (per-layer) and a dedicated premultiplied final postprocess
  (whole-composition).
- Identity (all color defaults) must be byte-identical to today's output and must
  avoid the composition/shader path changes when unset.
- No package dependencies added anywhere.

## Pinned Decisions

1. **Model** — `public readonly record struct BaseVisualTransform` in Core
   (`FatzBomb.MediaControl/Api/`), immutable, with identity defaults; struct
   layout grows as members are added pre-1.0 (binary/ABI-relevant, like
   `MediaPlayerConfiguration`). All members are plain value properties; no nested
   mutable state.
2. **Session surface** — `IMediaControlSession.BaseVisualTransform { get; set; }`
   plus `event EventHandler<MediaValueChangedEventArgs<BaseVisualTransform>>?
   BaseVisualTransformChanged`, reusing the existing immutable
   `MediaValueChangedEventArgs<T>` (previous/current + `MediaChangeReason`)
   introduced by Stage 029B. Setter stores the value and raises the event only on
   actual change (existing `MediaEffects`-style raising discipline).
3. **Scope of application** — default (`IncludeOverlays = false`) applies the
   transform only to the Layer 0 base visual (composition `Background` layer:
   main video/image and background video/image). With `IncludeOverlays = true`
   and a non-identity transform, the same math is applied to the completed
   composition as a whole — wash, CDG overlay, subtitle overlay, and other
   overlays included — as a final postprocess; the transform is moved off Layer 0
   for that frame (never applied twice to the base visual).
4. **Color space** — all math operates on **straight normalized sRGB** [0,1].
   Premultiplied layer pixels are unpremultiplied (divide RGB by A, A unchanged)
   before the transform, and re-premultiplied (multiply RGB by A) after. Alpha is
   never modified by any stage. The whole-composition postprocess works on the
   already-premultiplied final buffer the same way (unpremultiply → transform →
   re-premultiply).
5. **Clamping** — every stage clamps each channel to [0,1] after the stage's math,
   in the fixed order below; intermediate values are never left unclamped across
   stages.
6. **Fixed operation order** (exact, immutable):
   1. YIQ hue rotation
   2. Rec.709 saturation
   3. **TV-style contrast gain** — `rgb *= Contrast` (black remains black)
   4. **Brightness offset** — `rgb += Brightness`
   5. Independent per-channel RGB gain (last)
   (Contrast runs before brightness; contrast is a multiplicative gain from
   black, not a pivot-around-0.5 curve.)
7. **Ranges and defaults** (pinned):
   - `RedGain` / `GreenGain` / `BlueGain` — `float` in [0, 2]; default `1`
     (neutral). `0` removes the channel entirely, `2` boosts to clip.
   - `HueDegrees` — `float` in [-180, 180]; default `0`.
   - `Brightness` — `float` in [-1, 1]; default `0`. (Player UI exposes a
     practical −0.25…0.25 range; the public model retains −1…1.)
   - `Contrast` — `float` in [0, 2]; default `1`. (Player UI exposes
     0.5…1.5; the public model retains 0…2.)
   - `Saturation` — `float` in [0, 2]; default `1`.
   - `IncludeOverlays` — `bool`; default `false`.
   - There is **no `Lightness` member**; the old HSL-lightness stage is removed
     from the model and the math. There are no tint or color-wash members.
8. **Per-channel RGB gain definition** (pinned) — final stage, per pixel:
   - `R' = clamp(R * RedGain)`, `G' = clamp(G * GreenGain)`,
     `B' = clamp(B * BlueGain)`, each to [0,1].
   - Gain `0` removes the channel: `GreenGain=0` + `BlueGain=0` leaves red-only;
     `RedGain=0` leaves cyan/aqua; all three `0` yields black.
   - Gain `1` is neutral; gain `2` doubles the channel and clips at 1.
9. **YIQ hue rotation** (pinned coefficients) — forward:
   `Y = .299R + .587G + .114B`, `I = .596R - .274G - .322B`,
   `Q = .211R - .523G + .312B`; rotate I/Q by the hue angle; inverse:
   `R = Y + .956I + .621Q`, `G = Y - .272I - .647Q`, `B = Y - 1.106I + 1.703Q`.
   Hue `0` is identity (no rotation).
10. **Rec.709 saturation** (pinned) — `luma = .2126R + .7152G + .0722B`, then
    `channel = luma + (channel - luma) * factor`. Factor `1` is identity.
11. **Contrast / brightness** (pinned) — contrast is a TV-style gain applied
    before brightness: `channel * contrast` (black stays black, no pivot shift);
    brightness is an additive offset: `channel + brightness`. Identity is
    contrast `1` / brightness `0`.
12. **Software quantization reference** — the software renderer quantizes the
    final premultiplied output to BGRA8 using **round-to-nearest away from zero**
    (`Math.Round(value, MidpointRounding.AwayFromZero)`), the same rounding the
    existing software stretch uses; this is the reference for parity tests.
13. **Identity fast path** — when the color math equals the defaults, software
    output must be byte-identical to pre-stage output and the composition/shader
    path must not change (no new layers, no new constants, no new uploads, no
    final postprocess). `IncludeOverlays = true` with all-neutral math is still
    identity (`IsIdentity` ignores `IncludeOverlays`). The D3D11 shader pipeline
    is only engaged for the base-visual layer when the transform is non-identity.
14. **Dirty signaling** — a transform change sets a dirty flag that triggers a
    re-render of the base-visual layer without re-uploading the source
    pixels: the source texture/layer is unchanged; only the transform constants
    change (see Risks).
15. **No package dependencies** — no new NuGet packages in Core, Software, D3D11,
    WPF, or Player.

## Core Math (exact, per pixel, straight normalized sRGB)

Given straight RGB channels in [0,1] (unpremultiplied; A carried unchanged):

1. **Hue (YIQ rotation)** — `HueDegrees != 0`:
   - `Y = .299R + .587G + .114B`
   - `I = .596R - .274G - .322B`
   - `Q = .211R - .523G + .312B`
   - Rotate: `θ = HueDegrees * π / 180`;
     `I' = I * cos(θ) - Q * sin(θ)`, `Q' = I * sin(θ) + Q * cos(θ)`
   - Inverse: `R = Y + .956I' + .621Q'`, `G = Y - .272I' - .647Q'`,
     `B = Y - 1.106I' + 1.703Q'`
   - Clamp each channel to [0,1].
2. **Saturation (Rec.709)** — `Saturation != 1`:
   - `luma = .2126R + .7152G + .0722B`
   - `R = luma + (R - luma) * Saturation`, same for G, B
   - Clamp each channel to [0,1].
3. **Contrast (TV-style gain)** — `Contrast != 1`:
   - `R = Clamp(R * Contrast)`, same for G, B.
   - Black stays black; there is no pivot around 0.5.
4. **Brightness (additive offset)** — `Brightness != 0`:
   - `R = Clamp(R + Brightness)`, same for G, B.
   - Runs **after** contrast.
5. **Per-channel RGB gain** — `RedGain != 1 || GreenGain != 1 || BlueGain != 1`:
   - `R = Clamp(R * RedGain)`, `G = Clamp(G * GreenGain)`,
     `B = Clamp(B * BlueGain)`.
   - Gain `0` removes the channel (Green/Blue `0` leaves red; Red `0` leaves
     cyan/aqua; all `0` is black); gain `1` is neutral; gain `2` boosts and
     clips.
6. Re-premultiply RGB by the (unchanged) alpha; quantize to BGRA8 with
   round-to-nearest away from zero.

In **whole-composition mode** the same six steps are applied once to the final
premultiplied buffer (per layer-0 pixel in software: unpremultiply each
premultiplied pixel → steps 1-5 → re-premultiply), so wash/CDG/subtitle/other
overlay pixels pass through the identical math and alpha is preserved.

## Phased Implementation

Phase 1 — pure math/models/tests: **implemented** — `BaseVisualTransform` and
`BaseVisualTransformMath` in Core plus the full unit-test suite
(`BaseVisualTransformTests`), including the fixed-order guard with the
TV-style contrast-before-brightness semantics.

Phase 2 — software exact pixels: **implemented** — `SoftwareBaseVisualTransform`
mirrors the Core reference math and is applied in `SoftwareCompositionRenderer`'s
background/base compositing path when non-identity (per-layer mode), and as a
final postprocess of the premultiplied buffer when `IncludeOverlays` is true
(whole-composition mode); exact-pixel fixtures were added to
`SoftwareCompositionRendererTests`.

Phase 3 — composition/coordinator/API/hardware-route invalidation:
**implemented** — `MediaCompositionFrame` / `MediaCompositionLayer` carry
`BaseVisualTransform` on the Layer 0 background descriptor only;
`MediaPresentationCoordinator.BaseVisualTransform` re-presents the current
composition on change; `IMediaControlSession.BaseVisualTransform` +
`BaseVisualTransformChanged` added; the hardware primary with a non-identity
transform routes through `RenderHardwareVideoCompositionFrame` instead of
direct `PresentNativeFrame`.

Phase 4 — D3D11 separate base-visual shaders + whole-composition postprocess:
**implemented** — dedicated `PSBaseVisual` entry point compiled to
`_baseVisualPixelShader` (per-layer non-identity base visual); dedicated
`PSBaseVisualPremultiplied` entry point compiled to
`_baseVisualPostPixelShader` (whole-composition overwrite pass); the existing
`PSBackground` (`_backgroundPixelShader`) remains for identity backgrounds and
the subtitle layer. Whole-composition mode composites into a per-target cached
offscreen B8G8R8A8 render target (`_postProcessTargets`, keyed by target ID,
resized/disposed as needed) before the premultiplied overwrite pass; the
DirectComposition overlay split is disabled in that mode. Shader compilation is
covered by `D3D11CompositionLayerStateTests.BaseVisualPixelShaderCompiles` and
`BaseVisualPremultipliedPixelShaderCompiles`.

Phase 5 — full validation/manual: **complete** — automated gates green
(Release 0 warnings/errors; 438 tests; format/Core/WPF/parity gates; Core,
Software, and Direct3D11 nupkg + snupkg creation, Direct3D11 pack re-verified
for the split-state fix; focused final review PASS). The manual static-image
Include Overlays off/on, loop icon, and both-renderer Player
flyout/final-composite visual/UI smoke passed on 2026-08-17.

## API Compatibility Note (pre-1.0)

- `IMediaControlSession` gains `BaseVisualTransform` (get/set) and
  `BaseVisualTransformChanged`, reusing the existing immutable
  `MediaValueChangedEventArgs<T>`. As with Stages 029A–029C interface expansion,
  this is an explicitly accepted pre-1.0 additive change; consumers deriving from
  `MediaControlPlayer` compile unchanged, direct `IMediaControlSession`
  implementers must add the members.
- `IMediaControlSession` also gains runtime `ResetPitchOnMediaLoad` and
  `ResetTempoOnMediaLoad` (bool get/set) so hosts can toggle the Stage 029B
  per-load resets live. `MediaControlPlayer.LoadBehavior.cs` (new partial)
  forwards them to the mutable `MediaFile.ResetPitchOnMediaLoad` /
  `ResetTempoOnMediaLoad` options, which `MediaFile.LoadCore` applies through
  `MediaEffects.ApplyLoadReset(...)` at each load; the Stage 029B
  `MediaPlayerConfiguration` flags remain and seed the initial values. Same
  additive pre-1.0 convention as above.
- `BaseVisualTransform` is a `readonly record struct`; the contract revision
  history (tint members → color wash → independent per-channel RGB gains;
  `Lightness` removed; the color-wash preset type/file dropped; 2026-08-11
  contrast/brightness order + `IncludeOverlays`) landed before 1.0, so the struct
  layout and session state are pre-1.0 (binary/ABI-relevant). Adding it is
  additive and source-compatible for consumers that use the identity default.
- The WPF adapter is untouched by this stage (no new adapter surface); the Player
  host adds validation UI only.

## Delivered Files

- `FatzBomb.MediaControl/Api/BaseVisualTransform.cs` (new) — the readonly record
  struct with pinned ranges/defaults (`RedGain`/`GreenGain`/`BlueGain` default
  `1`, `HueDegrees`/`Brightness` default `0`, `Contrast`/`Saturation` default
  `1`, `IncludeOverlays` default `false`; no `Lightness`, tint, or color wash);
  init accessors clamp and treat NaN as the identity default; `Identity` /
  `IsIdentity` (color members only).
- `FatzBomb.MediaControl/Api/IMediaControlSession.cs` — additive
  `BaseVisualTransform` property + `BaseVisualTransformChanged` event.
- `FatzBomb.MediaControl/Api/MediaControlPlayer.VisualTransforms.cs` (new,
  partial) — property/event implementation with change-only raising via
  `HostEventDispatcher`, delegating to `MediaPresentationCoordinator`.
- `FatzBomb.MediaControl/Core/Graphics/Composition/BaseVisualTransformMath.cs`
  (new, internal) — the pure per-stage reference math (identity fast path,
  unpremultiply / re-premultiply, fixed order: YIQ hue → Rec.709 saturation →
  TV-style contrast gain → brightness offset → independent per-channel RGB gain,
  clamping, away-from-zero quantization).
- `FatzBomb.MediaControl/Core/Graphics/Composition/MediaCompositionFrame.cs` +
  `MediaCompositionFrame.Layers.cs` + `MediaCompositionLayer.cs` — additive
  `BaseVisualTransform` state on the Background (Layer 0) descriptor only,
  identity-by-default.
- `FatzBomb.MediaControl/Core/Graphics/Composition/MediaCompositionLayerDirtyFlags.cs`
  — new `TransformChanged` flag (re-render without upload).
- `FatzBomb.MediaControl/Controllers/IMediaPresentationCoordinator.cs` +
  `MediaPresentationCoordinator.cs` — coordinator `BaseVisualTransform`
  (change-only, re-presents on change), frame-plan carry, and non-identity
  hardware-route gating.
- `FatzBomb.MediaControl.Rendering.Software/SoftwareBaseVisualTransform.cs`
  (new, internal) — deterministic exact-pixel transform path mirroring the Core
  reference, including the premultiplied-to-premultiplied final-buffer path.
- `FatzBomb.MediaControl.Rendering.Software/SoftwareCompositionRenderer.cs` —
  per-layer transform applied in the background/base draw only when non-identity
  (`IncludeOverlays = false`); whole-composition mode (`IncludeOverlays = true` +
  non-identity) draws all layers untransformed and runs `ApplyFinalTransform`
  over the completed premultiplied buffer; identity output byte-identical.
- `FatzBomb.MediaControl.Rendering.Direct3D11/Graphics/Composition/D3D11MediaCompositionPresenter.cs`
  — dedicated `PSBaseVisual` shader (`_baseVisualPixelShader`) and
  `PSBaseVisualPremultiplied` shader (`_baseVisualPostPixelShader`), extended
  constant buffer (`TransformAdjustments` / `TransformBlend` /
  `TransformRgbGains`); per-target cached offscreen B8G8R8A8 postprocess targets
  (`_postProcessTargets`, keyed by target ID, resized/disposed);
  `CompileShaderBytecode` / `CdgShaderSource` internal for the compile tests.
- `FatzBomb.MediaControl.Rendering.Direct3D11/Graphics/Composition/D3D11/D3D11CompositionLayerState.cs`
  — transform-change diff → `TransformChanged | RenderRequired`, no
  `UploadRequired`.
- `FatzBomb.MediaControl.Rendering.Direct3D11/Graphics/D3D/Presentation/D3D11RendererPresentationServices.cs`
  — DirectComposition overlay split disabled when the frame carries a
  non-identity `IncludeOverlays` transform; the cached
  `DirectCompositionOverlaySplitBaseState` now includes `BaseVisualTransform`
  (root-cause fix: transform changes on static images were skipped because the
  split base state omitted it, while per-frame video revisions masked the bug).
- `FatzBomb.MediaControl.Player/MainWindow.xaml` (reworked; 307 lines) —
  persistent bottom chrome: timeline/seek, transport (Open/Play/Pause/Stop/Loop
  icon button with accent highlight via `ToggleChromeButtonStyle` +
  `LoopPlayback` Tag), one-at-a-time flyout toggle buttons (Sync/Video/Audio/A-V,
  active highlight via `Tag` + `FlyoutButtonStyle` trigger), volume, fullscreen;
  thin status strip; flyout content is the `PlayerFlyoutPanel` control.
- `FatzBomb.MediaControl.Player/Controls/PlayerFlyoutPanel.xaml` (new; 371
  lines) + `PlayerFlyoutPanel.xaml.cs` (new) + `Converters/FlyoutVisibilityConverter.cs`
  (new) — the four flyout panels driven by `ActiveFlyout`; the Video flyout
  exposes the transform sliders, the **Include Overlays** checkbox, and Reset
  Visual (see "Player Host UI" below). Loop Playback is **not** in the A/V
  flyout — it lives on the transport strip icon button (duplicate removed
  2026-08-11).
- `FatzBomb.MediaControl.Player/ViewModels/MainWindowViewModel.VisualTransforms.cs`
  (new partial) — transform slider/checkbox properties clamped to the practical
  host ranges (brightness −0.25…0.25, contrast 0.5…1.5, gains 0–2, hue −180…180,
  saturation 0–2), `IncludeOverlays`, `ResetBaseVisualTransformCommand`, and the
  `CreateBaseVisualTransform` assembly pushed to the session (change-only).
- `FatzBomb.MediaControl.Player/ViewModels/MainWindowViewModel.cs` +
  `App.xaml.cs` — startup flags (`--visual-transform-demo`,
  `--reset-pitch-on-load`, `--reset-tempo-on-load`, `--no-auto-hide`) seed the
  validation controls; the host now always sets `ShowMidiLyrics = true` (the MIDI
  lyrics checkbox was removed); status-line reporting
  (`Renderer | Presentation | AutoHide | Visual transform | Load resets`).
- `FatzBomb.MediaControl.Tests/BaseVisualTransformTests.cs` (new),
  `D3D11CompositionLayerStateTests.cs` (new),
  `D3D11RendererPresentationServicesTests.cs` (new) — math, conformance,
  layer-carry, shader-compile, render-without-upload, whole-composition,
  split-path base-state, and change-only event coverage, plus additions to
  `SoftwareCompositionRendererTests.cs`, `MediaCompositionFrameTests.cs`, and
  `MediaControlPlayerTests.cs`.

## Player Host UI (validation host, not production UI)

The Player host keeps a **persistent bottom chrome** (timeline/seek bar,
transport buttons, volume, fullscreen, status strip) and exposes four
**one-at-a-time flyout** panels via toggle buttons (Sync, Video, Audio, A/V).
Only one flyout is open at a time (`ActiveFlyout`; toggling the active button
again closes it; the active button is highlighted via `Tag` +
`FlyoutButtonStyle`). Opening fullscreen or starting playback closes the flyout.
Flyout content is extracted to `Controls/PlayerFlyoutPanel.xaml` and bound
through `FlyoutVisibilityConverter`.

- **Sync flyout** — A/V, CDG, and SUB ±100ms offset buttons plus Reset (per-item
  timeline sync, unchanged from earlier stages).
- **Video flyout** — live, Core-backed transform controls: HUE (−180°…180°,
  snapped to 15°), SATURATION (0–2), BRIGHTNESS (−0.25…0.25), CONTRAST
  (0.5–1.5), RED GAIN / GREEN GAIN / BLUE GAIN (0–2) sliders, an **Include
  Overlays** checkbox (TwoWay to `BaseVisualTransform.IncludeOverlays`), and a
  **Reset Visual** button (`ResetBaseVisualTransformCommand`) that restores every
  transform control to identity (gains `1/1/1`, hue/brightness `0`,
  contrast/saturation `1`, Include Overlays unchecked) and applies it. Every
  change assembles a `BaseVisualTransform` and pushes it straight to
  `MediaControlPlayer.BaseVisualTransform` (change-only; re-render without
  upload). There is **no Lightness slider**; the public model keeps the wider
  Brightness −1…1 and Contrast 0…2 ranges while the host UI exposes practical
  −0.25…0.25 and 0.5…1.5 ranges. There is **no color-wash or preset control** —
  the per-channel gains replace the former wash model.
- **Audio flyout** — PITCH and TEMPO sliders, Normalize checkbox, and the
  runtime **Reset Pitch on Load** / **Reset Tempo on Load** checkboxes
  (additive `IMediaControlSession` properties; see API Compatibility Note).
- **Transport strip loop (2026-08-11)** — Loop Playback moved from the A/V
  flyout to the persistent transport strip as a stateful icon button: it binds
  `ToggleLoopCommand` and `Tag="{Binding LoopPlayback}"` with
  `ToggleChromeButtonStyle`, so the accent highlight tracks the live loop state.
  The duplicate A/V flyout Loop checkbox was removed.
- **A/V flyout** — Auto Play, Auto Hide (Stage 029C runtime toggle), Secondary
  Window toggle (projector-output validation), video DISPLAY mode, presentation
  MODE (enabled before load only), and CDG background opacity. (Loop Playback
  moved to the transport strip icon button on 2026-08-11.)
- **MIDI lyrics** — the host always sets `ShowMidiLyrics = true`; the former MIDI
  lyrics checkbox was removed, so MIDI/KAR karaoke lyrics display automatically
  whenever the loaded file contains them.
- **Independence** — every control works without any startup flag. The existing
  flags are retained purely as compatibility initializers that seed their
  corresponding controls (`App.*` → `InitializeValidationControls`):
  `--visual-transform-demo` seeds the demo values (`RedGain = 0.85`,
  `GreenGain = 0.95`, `BlueGain = 1.2`, `HueDegrees = 30`, `Brightness = 0.05`,
  `Contrast = 1.15`, `Saturation = 1.2`; no lightness, Include Overlays off),
  `--reset-pitch-on-load` / `--reset-tempo-on-load` check the load-reset
  checkboxes, and `--no-auto-hide` unchecks Auto Hide. The seeded values flow
  through the same live path as manual edits.
- The status line reports the live state, e.g.
  `Renderer: Direct3D11 | Presentation: NativeHwndOverlay | AutoHide=True |
  Visual transform=ACTIVE | Load resets: Pitch=True, Tempo=False | Native DLL
  path: ...` (or `Renderer: Software` with `--software`).

Manual checklist: `docs/VALIDATION.md` (Stage 030A). This is deliberately not
polished production UI; host-facing polish is out of scope.

## Boundary

- No new WPF-adapter references to renderer/pipeline/internal-Cdg/D3D11
  namespaces; the adapter gains nothing in this stage.
- Core owns the model, math, and composition-plan carry; Software and D3D11 each
  implement the same public contract independently; no renderer internals leak
  into Core.
- `scripts/check-core-boundary.ps1` / `check-wpf-boundary.ps1` /
  `check-presentation-parity.ps1` must pass with no allow-list changes unless an
  explicit, reviewed exception is documented here.

## Tests

Implemented in `FatzBomb.MediaControl.Tests`:

- `BaseVisualTransformTests.DefaultsAreIdentity` — all members at pinned
  defaults: gains `1`, hue/brightness `0`, contrast/saturation `1`,
  `IncludeOverlays` `false`, no `Lightness` / tint / color-wash member.
- `BaseVisualTransformTests.ValuesAreClampedAndNaNUsesIdentityDefault` —
  clamping to the pinned ranges; NaN falls back to the identity default.
- `BaseVisualTransformTests.ExplicitNeutralGainsAreIdentity` — gains set to
  exactly `1/1/1` (with neutral hue/brightness/contrast/saturation) equal
  `Identity`.
- `BaseVisualTransformTests.IncludeOverlaysDoesNotMakeNeutralColorMathNonIdentity`
  — `IncludeOverlays = true` with all-neutral color math stays `IsIdentity` and
  byte-identical (identity bypass).
- Per-stage math (`BaseVisualTransformTests`):
  `TvStyleContrastScalesFromBlackBeforeBrightnessOffset` (contrast multiplies
  from black and runs before the brightness offset),
  `HueRotationUsesPinnedYiqEquations`,
  `SaturationZeroUsesRec709Luma`, `RgbGainsIndependentlyRemoveChannels` (gain 0
  removes the channel; red-only and cyan/aqua cases),
  `BrightnessContrastAndRgbGainsFollowFixedOrder` (fixed-order guard).
- `BaseVisualTransformTests.PremultipliedTransformPreservesAlpha` and
  `PremultipliedIdentityIsByteIdentical` — unpremultiply/re-premultiply with
  alpha unchanged; identity output byte-identical.
- `BaseVisualTransformTests.SoftwareMathMatchesCoreReference` — cross-reference
  conformance: `SoftwareBaseVisualTransform` output equals the Core reference
  math across 5 transforms × 3 pixels.
- `BaseVisualTransformTests.SoftwarePremultipliedMathMatchesCoreReference` —
  cross-reference conformance for the whole-composition final-buffer path
  (premultiplied-to-premultiplied).
- Software exact-pixel fixtures (`SoftwareCompositionRendererTests`):
  `Render_IdentityTransform_IsByteIdentical`,
  `Render_RgbGainsTransformOnlyBackgroundAndPreserveAlpha`,
  `Render_TransformDoesNotReachSubtitle`, `Render_HueUsesPinnedYiqRotation`,
  `Render_SaturationUsesPinnedRec709Luma`,
  `Render_RgbGainsIndependentlyRemoveChannels`,
  `Render_CombinedTransformUsesFixedOrder`,
  `Render_IncludeOverlaysTransformsCompletedComposition`,
  `Render_IncludeOverlaysDoesNotDoubleApplyBackgroundTransform`,
  `Render_IncludeOverlaysWithNeutralMathIsByteIdentical` — all vs the
  away-from-zero BGRA8 reference.
- `MediaCompositionFrameTests.BackgroundLayerCarriesOnlyBaseVisualTransform` —
  Layer 0 carry only; every other layer stays identity.
- `MediaControlPlayerTests.BaseVisualTransformChangedFiresOnlyOnActualChange` —
  event raised only on actual change; previous/current payload correct.
- `D3D11CompositionLayerStateTests.TransformChangeRequiresRenderWithoutUpload` —
  transform change sets `TransformChanged | RenderRequired` with no
  `UploadRequired`.
- `D3D11CompositionLayerStateTests.BaseVisualPixelShaderCompiles` +
  `BaseVisualPremultipliedPixelShaderCompiles` — `PSBaseVisual` and
  `PSBaseVisualPremultiplied` compile to `ps_5_0` bytecode via `D3DCompile`.
- `D3D11RendererPresentationServicesTests.DirectCompositionBaseStateChangesWhenStaticImageTransformChanges`
  — regression pin for the split-path root-cause fix: two
  `DirectCompositionOverlaySplitBaseState` values built from the same
  frame/target differ when only `BaseVisualTransform` changes (RedGain 0.5 vs
  identity), so a transform change on a static image invalidates the cached
  split base state and re-presents.
- Hardware-route gating is implemented in
  `MediaPresentationCoordinator.PresentPrimaryVideoFrame` (non-identity →
  `RenderHardwareVideoCompositionFrame`); the GPU path itself is manual
  verification territory.

## Acceptance Criteria

- `BaseVisualTransform` exists as an immutable readonly record struct with the
  pinned ranges/defaults (`RedGain`/`GreenGain`/`BlueGain` [0,2] default 1,
  hue/brightness/contrast/saturation, `IncludeOverlays` default `false`; no
  `Lightness`, tint, or color wash); identity produces byte-identical output on
  both renderers with no composition/shader path change.
- Default mode applies only to the Layer 0 base visual (main video/image and
  background video/image); `IncludeOverlays = true` + non-identity applies the
  same math to the completed composition (wash, CDG, subtitle, other overlays)
  in a final postprocess that moves — never double-applies — the transform.
- Software output matches the pinned equations exactly (round-away-from-zero
  BGRA8 reference) in the fixed order (YIQ hue → Rec.709 saturation → TV-style
  contrast gain → brightness offset → independent per-channel RGB gain); D3D11
  parity passes exact-pixel comparisons.
- `IMediaControlSession.BaseVisualTransform` + `BaseVisualTransformChanged`
  behave per the change-only contract; direct interface implementers see the
  additive members.
- Transform changes re-render without source re-upload; hardware primary
  zero-copy routes through composition only when non-identity.
- No package dependencies; Release build 0 warnings; full suite green (438
  tests); boundary and parity gates pass; focused final review PASS; Player
  manual checks pass on both renderer paths (static-image Include Overlays
  off/on, loop icon, both renderers; confirmed 2026-08-17).

## Validation

- Release build passed with 0 warnings/errors.
- 438 automated tests passed.
- `dotnet format --verify-no-changes`, Core boundary, WPF boundary, and
  presentation-parity gates passed.
- Core, Software, and Direct3D11 nupkg + snupkg creation passed.
- Focused final review passed.
- Manual Player smoke passed on 2026-08-17 for static-image transform
  invalidation, Include Overlays off/on, the loop icon, flyout behavior, and
  visually equivalent Direct3D11 and software-renderer output.

## Critical Risks

- **Subtitle shares the D3D background shader** — today the subtitle layer is
  drawn with `_backgroundPixelShader` in `D3D11MediaCompositionPresenter`
  (subtitle pass binds the same background pixel shader). Per-layer transforms
  must therefore use a **new, dedicated base-visual shader**; the shared
  background shader must keep its current semantics or subtitles would be
  transformed incorrectly. Whole-composition mode deliberately routes through
  the final postprocess instead so overlay text is transformed only when
  `IncludeOverlays` asks for it.
- **No double-apply in whole-composition mode** — when `IncludeOverlays` is true
  and non-identity, both renderers must draw the base visual with identity and
  apply the transform exactly once in the final pass; applying it in both places
  would darken/color-shift the base visual twice. Covered by
  `Render_IncludeOverlaysDoesNotDoubleApplyBackgroundTransform`.
- **Per-target cached offscreen target lifecycle** — the D3D11 whole-composition
  path composites into a per-target cached B8G8R8A8 render target keyed by
  target ID; it must be recreated when the target is resized and disposed on
  reset/device-loss, or stale-size artifacts or resource leaks appear.
- **DirectComposition overlay split** — the split-path (separate base/overlay
  DirectComposition visuals) is incompatible with whole-composition
  postprocessing and must be disabled when a non-identity `IncludeOverlays`
  transform is active, falling back to the composed path. The split's cached
  base state must also carry `BaseVisualTransform` — with `IncludeOverlays =
  false`, a static image/background base presents only when its cached
  `DirectCompositionOverlaySplitBaseState` changes, and the state previously
  omitted the transform, so transform changes were skipped for static images
  while per-frame video revisions masked the defect. Fixed 2026-08-11 and
  pinned by
  `DirectCompositionBaseStateChangesWhenStaticImageTransformChanges`.
- **Hardware primary zero-copy** — the primary video path can present hardware
  frames directly through `D3D11VideoProcessorPresenter`; a non-identity
  transform must force that path through the composition/shader route, and the
  route must be selected from the transform state before presentation, not
  reactively.
- **TransformChanged dirty flag** — a change must mark the base-visual layer dirty
  so the coordinator re-renders **without re-uploading the source**; getting this
  wrong either stalls the update or adds per-change upload cost. Pinned in the
  planning/dirty logic.
- **Identity regressions** — any composition-plan or shader change risks altering
  byte-identical identity output; the identity fast path must avoid the new
  path entirely. All gains at `1` with neutral hue/brightness/contrast/
  saturation must be identity even when `IncludeOverlays` is true.
- **Rounding drift** — software (away-from-zero) and D3D11 (GPU) quantization must
  agree on the parity fixtures; the software output is the reference, and any GPU
  variance must be characterized, not silently accepted.
- **No package dependencies** — the math must be self-contained; do not add
  color-space or matrix libraries.

## Explicit Non-Goals

- Transforming overlays (wash, CDG, subtitles, karaoke text, other overlays) in
  the default mode — that is opt-in via `IncludeOverlays = true`.
- Per-clip or animated transform keyframes.
- Color management, gamut mapping, or HDR tone mapping.
- Per-renderer reinterpretation of the Core transform contract.
- Polished production host UI for the transform controls (validation affordance
  only).
- HSL/HSV lightness adjustment as a transform member.
- Tint or color-wash blend members; per-channel RGB gain is the channel-level
  mechanism.

## Coordination

- Closed 2026-08-17 after automated validation and user-confirmed manual smoke.
  Consumes the Stage 029B `MediaValueChangedEventArgs<T>` convention. Recommended
  next stage:
  `future/030B_IDEA_SilenceTrimmingAnalysisCache.md`.
