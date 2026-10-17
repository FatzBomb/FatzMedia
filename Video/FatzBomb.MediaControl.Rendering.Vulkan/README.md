# FatzBomb.MediaControl.Rendering.Vulkan

Preview Vulkan renderer backend for `FatzBomb.MediaControl` (Stage 026A slices 1-2).

```csharp
if (VulkanPresentationCoordinatorFactory.IsSupported(out var reason))
    factory = new LinuxMediaControlPlatformFactory(new VulkanPresentationCoordinatorFactory());
```

The backend renders into a managed BGRA frame-sink surface: GPU clear, background upload and
nearest blit (same placement rules as the software renderer), then a SPIR-V render pass that
blends the wash and the CDG overlay (palette-expanded on the CPU, nearest-sampled with the
software renderer's UniformFit mapping), and readback. Frames with subtitles, base-visual
transforms, or native/retained backgrounds are rejected with a diagnostic. Window swapchains
are a later slice.

## Shaders

GLSL sources live in `Shaders/*.vert|*.frag`. The compiled SPIR-V (`*.spv`) is committed and
embedded into the assembly because CI machines do not have glslang. After editing a shader,
run `scripts/compile-vulkan-shaders.sh` (needs `glslangValidator`, Debian/Ubuntu package
`glslang-tools`) and commit the regenerated `.spv` files.
