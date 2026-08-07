# FatzBomb.MediaControl.Rendering.Software

Portable deterministic renderer for `FatzBomb.MediaControl` composition frames.

`SoftwareCompositionRenderer` converts a software-only `MediaCompositionFrame` into
an in-memory premultiplied BGRA8 frame. It supports clear modes, software BGRA
backgrounds, deterministic nearest-neighbor stretch modes, wash colors, CDG overlays,
and pre-rasterized subtitles.

The package also supplies `SoftwarePresentationCoordinatorFactory` for explicit host
selection through the same renderer seam as Direct3D11. Its backend accepts one
`ManagedBgraFrameSink` presentation surface. Native video resources and retained GPU
backgrounds are intentionally unsupported.

Composition behavior is deterministic:

- output rows are tightly packed (`stride = width * 4`);
- output pixels use premultiplied BGRA8;
- destination edges round away from zero;
- scaled pixels use nearest-neighbor sampling at destination pixel centers;
- non-background CDG palette colors are opaque, matching the Direct3D11 shader;
- `PreservePrevious` clears to transparent because rendering is stateless.
