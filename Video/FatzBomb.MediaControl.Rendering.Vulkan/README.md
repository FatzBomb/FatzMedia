# FatzBomb.MediaControl.Rendering.Vulkan

Preview Vulkan renderer backend for `FatzBomb.MediaControl` (Stage 026A slice 1).

```csharp
if (VulkanPresentationCoordinatorFactory.IsSupported(out var reason))
    factory = new LinuxMediaControlPlatformFactory(new VulkanPresentationCoordinatorFactory());
```

Slice 1 renders into a managed BGRA frame-sink surface: GPU clear, background upload and
nearest blit (same placement rules as the software renderer), readback. Frames with
wash, CDG, subtitles, base-visual transforms, or native/retained backgrounds are rejected
with a diagnostic. Window swapchains and shader composition are the next slices.
