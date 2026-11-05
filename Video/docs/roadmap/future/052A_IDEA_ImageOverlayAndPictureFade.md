# Stage 052A (planned) - Host image overlay layer and picture fade

Owner decision 2026-11-05. Queue item 3 (047A may go first after its step 0).

- `player.SetOverlayImage(bgra, width, height, stride, placement)` (fill, fit or rectangle; opacity)
  and `ClearOverlayImage()`: a new composition layer above video, CD+G/lyrics and subtitles, BGRA
  pixels with a revision (uploaded once), drawn by Direct3D11, Vulkan and Software, so it works in
  native and composited mode and on second screens. Nothing drawn or checked when unset.
- "Change together": the image applies on the first frame of a newly loaded media, so screen and
  video switch in one frame (native mode too). Suits screens that change occasionally; animated
  screens stay WPF content in composited mode.
- `FadePictureAsync(opacity, duration)`: ramps the compositor's layer opacity, a video fade for
  native mode and second screens. (In composited mode a host fades its root panel's `Opacity`.)
- Parity: `check-presentation-parity.ps1` updated for the new layer.
