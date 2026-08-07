# Idea Stage 026A - Cross-Platform Renderer Backends

Status: Good idea. Recommended after Stage 024A.

## Why This Is Worth Doing

The renderer-neutral Core contracts now support independently packaged backends. Vulkan,
OpenGL, and Metal implementations would broaden OS and GPU support, validate the public
renderer boundary, and let hosts select a backend without changing playback or UI adapters.

## Recommended Package Direction

- `FatzBomb.MediaControl.Rendering.Vulkan` for modern Windows and Linux GPU presentation.
- `FatzBomb.MediaControl.Rendering.OpenGL` for broad compatibility where it earns its support cost.
- `FatzBomb.MediaControl.Rendering.Metal` for native macOS presentation.
- Keep each backend independently selectable; do not create a universal renderer package.

## Recommended Implementation Order

1. Complete the deterministic software renderer in
   `../active/024A_ACTIVE_SoftwareRenderer.md` as the composition reference.
2. Build a renderer capability and native-surface compatibility matrix.
3. Start one Vulkan vertical slice: initialize, attach one surface, clear, and present software BGRA.
4. Add composition layers, resize, multi-target fan-out, pacing, and device-loss behavior incrementally.
5. Reuse the same conformance suite for later OpenGL and Metal backends.

## Recommended Immediate Action

Document how `IMediaRendererBackend` maps to Vulkan, OpenGL, and Metal for surface types,
swap chains, software uploads, native video resources, subtitle rasterization, pacing,
multi-target presentation, and device-loss recovery. Select the Vulkan clear-and-present
vertical slice as the first hardware implementation.

## Must Preserve

- Core renderer and composition contracts remain renderer-neutral.
- Platform packages remain independent from renderer packages.
- Native handles remain borrowed and are never released by Core.
- Multi-target output executes one Core composition plan consistently.

## Explicit Non-Goals

- Implementing all renderers in one stage.
- Changing Core contracts preemptively without a demonstrated backend incompatibility.
- Owning OS media services or UI-framework lifecycle inside renderer packages.
- Automatic cross-renderer fallback before lifecycle and performance policy are designed.
- Requiring Vulkan, OpenGL, or Metal dependencies in portable Core.
