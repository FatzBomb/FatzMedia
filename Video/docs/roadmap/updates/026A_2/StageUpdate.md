# Stage Update

Stage: 026A.2 - Vulkan SPIR-V pipeline: wash and CDG overlay
Date: 2026-10-17

## Status: IMPLEMENTED - validated headlessly on lavapipe; pending manual smoke

Delivered:

- `VulkanOverlayPipelines`: render pass (load/store, colour attachment), pipeline layout
  (one 16-byte push-constant range, one combined-image-sampler set), nearest clamp-to-edge
  sampler, descriptor pool/set, and two graphics pipelines (wash, CDG) sharing one vertex
  shader. Premultiplied ONE / ONE_MINUS_SRC_ALPHA blending for colour and alpha. Created
  once at device initialization, destroyed in `Dispose`.
- `Shaders/quad.vert`, `wash.frag`, `cdg.frag` (GLSL 450) with committed SPIR-V embedded
  as resources; `scripts/compile-vulkan-shaders.sh` regenerates the `.spv` files
  (`glslangValidator -V --target-env vulkan1.0`). CI does not need glslang.
- `VulkanOffscreenCompositor`: the target gains `ColorAttachment` usage, an image view and a
  framebuffer (re-created with the target size). Per frame: clear -> optional background blit
  -> (wash/CDG) barrier transfer -> colour attachment, render pass, barrier colour attachment
  -> transfer source -> readback. The CDG image (300x216 BGRA8) is re-expanded and uploaded
  each frame it is drawn.
- `VulkanCompositionPlanner`: `Plan` builds a `VulkanFramePlan` (clear, background blit,
  wash, CDG layer); `PlanOverlay` reproduces `SoftwareStretch.Create` (UniformFit destination,
  edges rounded away from zero and clipped, same source-pixel formula) and yields the texture
  coordinates; `ExpandCdg` expands indices like `SoftwareCompositionRenderer.DrawCdg`.
  `Classify` now rejects only `Subtitles`, `BaseVisualTransform`,
  `NativeOrRetainedBackground`, and `InvalidCdgSnapshot` (the software renderer refuses the same
  snapshots). The backend diagnostic lists what is supported.
- Device selection additionally requires `ColorAttachment`, `ColorAttachmentBlend`, and
  `SampledImage` for B8G8R8A8Unorm (optimal tiling); the failure message names each rejected
  device and its missing features.
- Tests: `FatzBomb.MediaControl.Rendering.Vulkan.Tests` 17 -> 50 cases (planner
  classification, plan, overlay mapping and texture-coordinate interpolation, CDG expansion;
  GPU conformance for wash only, background + wash, CDG only at seven output sizes, CDG at
  texel-edge scales, background + wash + CDG, resize between overlay frames, backend
  presenting a wash + CDG frame and rejecting a subtitle frame).

## Tolerances measured (llvmpipe, LLVM 20.1.2, Vulkan 1.4)

- Wash over a uniform clear: exact (largest channel delta 0).
- Wash over the gradient background, and background + wash + CDG: largest channel delta 1.
  lavapipe blends 8-bit targets in fixed point, rounding the source term (s*a/255) and the
  destination term (d*(255-a)/255) separately; `BlendStraight` rounds their sum once. Tests
  allow +/-1 per channel wherever a wash is drawn (`WashTolerance = 1`). A float blender
  matches the software formula exactly.
- CDG only: exact at 300x216, 600x432, 900x648, 640x480 (fractional top edge), 1280x720,
  1920x1080, and 333x250.
- CDG at 450x324 (1.5x) and 150x108 (0.5x): 85.4 % and 52.5 % exact. At these scales pixel
  centres land exactly on source texel edges; the software renderer picks a side by
  double-precision rounding noise, the GPU by fp32 interpolation. Every pixel stays within
  one source texel of the software choice (asserted by
  `CdgAtTexelEdgeScales_StaysWithinOneSourceTexel`).

## Decisions made without the owner

- One push-constant `vec4` at offset 0 serves both pipelines (vertex stage: CDG texture
  rectangle; wash fragment stage: wash colour). With separate `vec4`s at offsets 0 and 16,
  lavapipe returned the offset-0 bytes for the fragment-stage `vec4` load at offset 16
  (scalar loads were correct), which drew no wash.
- CDG sampling uses a nearest sampler with planner-computed texture coordinates (as
  specified) rather than per-column/per-row index lookups. Lookups would make the
  texel-edge scales bit-exact; recorded as a possible follow-up if parity at those scales
  matters.
- Malformed CDG snapshots (index buffer shorter than 300x216, or indices without a palette)
  get their own `InvalidCdgSnapshot` classification instead of being drawn, mirroring the
  software renderer's `InvalidFrame` result.
- The CDG texture is re-expanded and re-uploaded on every drawn frame (about 260 KB);
  revision-based caching can follow once a real host drives the backend.
- `.gitattributes` marks `*.spv` as binary.

## Validation (Linux sandbox)

- `EnableWindowsTargeting=true dotnet build FatzBomb.MediaControl.sln -c Release`: 0 warnings,
  0 errors.
- `dotnet test FatzBomb.MediaControl.Rendering.Vulkan.Tests -c Release`: 50/50 on lavapipe.
- `dotnet test FatzBomb.MediaControl.Portable.Tests -c Release`: 738/738.
- `EnableWindowsTargeting=true dotnet format FatzBomb.MediaControl.sln --verify-no-changes`:
  clean.
- Boundary scripts: not run in this sandbox (the worktree isolation guard refuses `pwsh`).
  No project references, Core, WPF, or adapter files changed, so their inputs are the same as
  the previous passing run; Windows CI runs them.

## Manual smoke

None needed beyond CI; Vulkan is not used by the Windows Player. Optionally, on a machine with
a real Vulkan GPU, run `dotnet test FatzBomb.MediaControl.Rendering.Vulkan.Tests`: all pass and
the conformance messages (on failure) name the GPU rather than llvmpipe.

## Review follow-up (same day, local code review)

- Boundary scripts, which the isolated worktree could not run, pass on the integrated
  branch; Release build, format, and all four Linux-runnable test projects pass.
- Not changed: overlay pipeline creation stays part of compositor initialization. A failure
  there now disables Vulkan entirely rather than only wash/CDG frames; making it optional
  would add guards around framebuffer creation and drawing for a failure the tests already
  catch (missing SPIR-V) or that is unlikely on a device that passed the format checks.
- Not changed: the ±1 wash tolerance and the texel-edge CDG scales (both above).
- A reported overflow in the CDG pixel count does not apply: snapshots are always 300x216.
- Duplicated placement/validation, the SPIR-V freshness check, and CDG upload caching are
  recorded as ENG-014.

