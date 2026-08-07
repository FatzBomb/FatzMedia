# Active Stage 024A - Deterministic Software Renderer

Prepared: 2026-08-07
Status: Implemented — automated validation passed 2026-08-07; final manual Direct3D11
Player regression smoke pending. Not yet complete; do not archive.

## Objective

Add a portable, deterministic software renderer that turns Core composition plans
(`MediaCompositionFrame`) into in-memory BGRA pixels without a GPU or live window.
This unlocks automated visual-regression tests, thumbnails, offline frame export, and
headless composition, and gives future hardware backends a reference implementation
for exact composition behavior. Stage 024A delivers the first bounded vertical slice:
one fixed-size in-memory frame, software-backed composition inputs only, and
deterministic BGRA output verified by exact-pixel tests.

## Architecture Ownership

- **Core owns composition policy.** The software renderer consumes
  `MediaCompositionFrame` (and `MediaCompositionLayer` layer intent) as produced by
  Core's compositor; it must not duplicate, reinterpret, or re-own layer/planning
  policy. Core's existing `MediaPresentationCoordinator` continues to own composition
  planning and backend dispatch. Per-target fan-out remains renderer-package-owned;
  this slice supports one software frame-sink target.
- **Renderer package is portable.** `FatzBomb.MediaControl.Rendering.Software`
  references Core only. No UI framework, OS platform package, GPU/DXGI, audio, or
  timing integration.
- **Renderer selection stays explicit.** The renderer implements the existing public
  `IMediaRendererBackend` contract from Core; hosts choose it by injecting its
  coordinator factory, exactly as with the Direct3D11 package. No Core or Windows
  code changes for renderer selection.
- **Existing Direct3D11 behavior is preserved.** No change to Direct3D11 package
  selection, defaults, or presentation paths in this stage.

## First-Slice Scope (exact)

Produce one complete in-memory BGRA frame from a software-only
`MediaCompositionFrame`:

- Clear: `MediaCompositionClearMode.Black`, `Transparent`, and `ConfiguredColor`
  from `ClearMode` / `ClearArgb`. `PreservePrevious` has explicit slice-one behavior.
- Software BGRA background: `BackgroundBgra` / `BackgroundWidth` /
  `BackgroundHeight` / `BackgroundStride` with `EffectiveBackgroundStretchMode`
  (`UniformFit`, `UniformToFill`, `Fill`, and `Original`; `Auto` resolves through
  the existing effective-mode property).
- Wash: `WashArgb` alpha-blended over the background.
- CDG overlay: `CdgOverlay` indexed color/palette snapshot composited on top,
  honoring transparent-color behavior.
- Pre-rasterized subtitle overlay: `SubtitleBgra` at `SubtitleCanvas*` placement.
- Layer ordering is fixed: clear, background, wash, CDG, subtitles. The slice does
  not implement retained-layer scheduling, multi-target fan-out, or dirty-region
  diffing.
- Inputs outside the slice (`BackgroundNativeResource`, `UseRetainedBackground`, and
  shader-only paths) are not part of the first slice; frames requesting them return
  a defined "not supported" result or skip the unsupported component, documented
  and tested.

## Deterministic Output Contract

- Pixel format: BGRA8 with exact byte-order and stride rules. Per-component blend
  equations are explicit: background and wash use straight-alpha behavior matching
  the Direct3D11 base-layer pass; CDG and subtitle overlays use premultiplied-alpha
  behavior matching the Direct3D11 overlay passes.
- Color space: sRGB; no color-management transforms in this stage.
- Dimensions: caller-fixed output size; scaling rules for background and subtitle
  placement are defined exactly — nearest-neighbor sampling at destination pixel
  centers and destination edges rounded half-away-from-zero (`Math.Round(...,
  MidpointRounding.AwayFromZero)`) — and applied consistently.
- Alpha behavior: explicit blend equations for wash and CDG/subtitle overlays,
  including transparent-color handling, defined in the contract and covered by tests.
  CDG transparency is index-based: the background index is skipped and every other
  palette entry is forced opaque, matching the Direct3D11 shader.
- Determinism: same input frame + same size always produces byte-identical output;
  no threading, wall-clock, or hash-order dependence. Output rows are tightly packed
  (`stride = width * 4`) and pixels are premultiplied BGRA8.

## Delivered Files / Projects

- `FatzBomb.MediaControl.Rendering.Software/` — new project (`net10.0`, references Core
  only; `InternalsVisibleTo` Tests; packable with nupkg + snupkg).
  - `SoftwareCompositionRenderer.cs` — deterministic frame-to-buffer API taking a
    `MediaCompositionFrame` and fixed output dimensions.
  - `SoftwareRendererBackend.cs` — internal `IMediaRendererBackend` adapter. Surface
    methods resolve one primary `ManagedBgraFrameSink`, capture its dimensions, and
    publish the rendered frame; native-frame/device methods report unsupported without
    throwing.
  - `SoftwareCompositionBuffer.cs` — fixed-size premultiplied BGRA8 target and
    straight/premultiplied blend operations.
  - `SoftwareStretch.cs` — exact background/subtitle stretch math (pure functions):
    nearest-neighbor sampling at destination pixel centers, destination edges rounded
    half-away-from-zero.
  - `SoftwarePresentationCoordinatorFactory.cs` — `IMediaPresentationCoordinatorFactory`
    implementation for host selection. It supplies Core's coordinator with the software
    backend, the no-op `ISubtitleRasterizer` (`NoOpSubtitleRasterizer`), and
    `SystemPrecisionWaiterFactory`.
  - `SoftwareRenderOutput.cs` — immutable in-memory frame result payload.
  - `SoftwareRenderStatus.cs` — typed result status (`Rendered`, `UnsupportedFrame`,
    `InvalidFrame`).
  - `NoOpSubtitleRasterizer.cs` — contract-required no-op rasterizer; returns empty
    results and performs no text layout.
- `FatzBomb.MediaControl.Tests/`
  - `SoftwareCompositionRendererTests.cs` — clear modes, stretch modes, wash alpha,
    layer order, CDG palette/transparency, subtitle placement, determinism,
    unsupported/invalid frames.
  - `SoftwareRendererBackendTests.cs` — managed frame-sink attach/present/detach and
    invalid-surface behavior.

## Validation Status (2026-08-07)

Automated validation passed:

- Debug and Release builds — zero warnings.
- 370 automated tests (`dotnet test`) — up from 348; the 22 new tests cover the
  software renderer's exact-pixel output contract and backend surface behavior.
- `dotnet format --verify-no-changes --no-restore`.
- Core boundary, WPF boundary, and presentation-parity gates
  (`scripts/check-core-boundary.ps1` updated to allow Software -> Core and Tests ->
  Software).
- `FatzBomb.MediaControl.Rendering.Software` nupkg and snupkg pack.

Remaining manual validation:

- Final Direct3D11 Player regression smoke (per `docs/VALIDATION.md` and step 7 of the
  implementation order below). Stage 024A stays active until it passes.

## Acceptance Criteria

- `FatzBomb.MediaControl.Rendering.Software` builds with zero warnings in Release
  and references Core only (no WPF, Windows, D3D11, audio, or timing).
- A software-only `MediaCompositionFrame` produces a byte-exact in-memory BGRA frame
  per the output contract.
- The backend accepts one valid `ManagedBgraFrameSink` surface, uses its pixel size,
  and sends the same deterministic output through `IMediaPresentationFrameSink`.
- All first-slice components (clear, background stretch modes, wash, CDG, subtitle
  placement) are covered by exact-pixel tests, plus a determinism test (two renders
  produce identical bytes).
- Unsupported inputs (native/reserved paths) return the defined result without
  throwing or corrupting output.
- `PreservePrevious`, `Auto`, zero-sized output, invalid stride, and invalid surface
  behavior are explicitly defined and tested.
- Core composition policy is unchanged; no Core file changes beyond what the stage
  explicitly requires for the public contract.
- Direct3D11 package and existing validation remain green.

## Focused Tests

- Exact-pixel fixtures for each `MediaCompositionClearMode` and clear color.
- Background stretch: UniformFit letterbox, UniformToFill crop, exact-fit, and
  rounding behavior at odd dimensions.
- Wash alpha blending over a known background.
- CDG palette mapping and transparent-color compositing.
- Subtitle overlay placement using `SubtitleCanvas*`.
- Layer ordering (wash over background, CDG over wash, subtitles on top).
- Determinism: identical bytes across repeated renders.
- "Unsupported input" behavior for native/reserved backgrounds.
- Managed frame-sink attach, resize, present, detach, and invalid-surface behavior.
- No-op subtitle-rasterizer factory wiring without text-layout dependencies.

## Validation Commands

```powershell
dotnet build FatzBomb.MediaControl.sln --no-restore
dotnet build -c Release FatzBomb.MediaControl.sln --no-restore
dotnet test FatzBomb.MediaControl.sln --no-restore
dotnet format --verify-no-changes --no-restore
.\scripts\check-core-boundary.ps1
.\scripts\check-wpf-boundary.ps1
.\scripts\check-presentation-parity.ps1
```

The stage updates `scripts/check-core-boundary.ps1` so the allowed project graph
includes Software -> Core and Tests -> Software. The WPF and presentation-parity
scripts require no new dependency allowance.

## Risks

- Stretch/rounding divergence from Direct3D11 output is possible; the software
  renderer defines its own exact contract and is the reference for conformance,
  so this is acceptable and documented rather than a bug.
- CDG transparent-color and alpha semantics must be pinned precisely to match the
  existing CDG pipeline; use `CdgRenderSnapshot.BackgroundColorIndex` and existing
  CDG tests as the semantic reference.
- `PreservePrevious` is stateful by definition while the first slice is stateless;
  its deterministic fallback must be documented rather than inferred.
- The renderer must not become a hidden playback fallback (see Non-Goals); host
  opt-in only.

## Explicit Non-Goals

- Automatic playback fallback in the first stage.
- Native GPU texture or hardware-decoder interop (including
  `BackgroundNativeResource`).
- A new text-layout or text-rasterization engine; only pre-rasterized
  `SubtitleBgra` is composited. The factory's contract-required no-op
  `ISubtitleRasterizer` returns an empty result and performs no text layout.
- Window, swap-chain, audio, timing, or host UI integration.
- A second copy of Core composition policy, planning, or target fan-out.
- Retained-layer scheduling, multi-target fan-out, or dirty-region diffing.
- Color management or non-BGRA output formats in this stage.

## Recommended Implementation Order

1. Add the `FatzBomb.MediaControl.Rendering.Software` project and its Core-only
   project reference; update the solution and boundary-script project map; define
   the output contract and result payload.
2. Implement clear + software BGRA background with exact stretch math and tests.
3. Add wash alpha blending and tests.
4. Add CDG overlay compositing with transparent-color handling and tests.
5. Add pre-rasterized subtitle placement and layer-order/determinism tests.
6. Implement the managed-frame-sink `IMediaRendererBackend` adapter and coordinator
   factory wiring for host opt-in. Supply the no-op `ISubtitleRasterizer` and
   `SystemPrecisionWaiterFactory` without changing Core selection policy.
7. Run the validation commands, update architecture/validation docs, and record
   manual confirmation of Direct3D11 regression-free behavior (pending as of
   2026-08-07; the automated portion has passed).
