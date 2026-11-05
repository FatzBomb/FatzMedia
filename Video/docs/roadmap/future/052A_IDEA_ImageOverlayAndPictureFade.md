# Stage 052A (planned) - Host image overlay layer and picture fade

Owner decision 2026-11-05. Queue item 3 (047A may go first after its step 0).

- `player.SetOverlayImage(bgra, width, height, stride, placement)` (fill, fit or rectangle; opacity)
  and `ClearOverlayImage()`: a new composition layer above video, CD+G/lyrics and subtitles, BGRA
  pixels with a revision (uploaded once), drawn by Direct3D11, Vulkan and Software, so it works in
  native and composited mode and on second screens. Nothing drawn or checked when unset.
- "Change together": the image applies on the first frame of a newly loaded media, so screen and
  video switch in one frame (native mode too). Suits screens that change occasionally; animated
  screens stay WPF content in composited mode.
- **Timed transitions (owner 2026-11-05, TriviaBomb):** one compositor mechanism for
  `AnimateVisualTransformAsync(BaseVisualTransform target, TimeSpan duration, VisualEasing easing =
  EaseInOut, CancellationToken ct = default)` and `FadePictureAsync(double opacity, TimeSpan duration,
  VisualEasing easing = EaseInOut, CancellationToken ct = default)` (both `Task<bool>`).
  - The compositor computes each value for the presented frame's own time, on every surface (second
    screens too): no per-frame UI-thread work, no per-frame events (`BaseVisualTransformChanged` at the
    start and the end only).
  - Every transform field animates together (hue, brightness, contrast, saturation, RGB gains); picture
    opacity is true layer opacity (shows what is behind the video), unlike a brightness ramp.
  - Easing: `Linear`, `EaseIn`, `EaseOut`, `EaseInOut` (cubic). Hue moves numerically (0 to 150 passes 75),
    with an option for the shorter way round (350 to 10 through 0).
  - A new animation or a direct set takes over smoothly from the current value; the replaced task returns
    `false`, a finished one `true`.
  - Until it exists, hosts set the transform from `CompositionTarget.Rendering` with their own easing
    (works, not frame-exact). In composited mode a host can also fade its root panel's `Opacity`.
- Parity: `check-presentation-parity.ps1` updated for the new layer.
