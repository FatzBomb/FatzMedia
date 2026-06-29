# Stage 10X / 11 Plan

This is the current source of truth for active presentation/compositor work.

## Non-Negotiable Goal

`FatzBombMedia.MediaControl` is the media-control engine. It owns playback, layers, compositor, swap chains, presentation targets, multi-target fan-out, and final presentation.

Adapters are framework interop packages only.

## Active Projects

- `FatzBombMedia.MediaControl`: Core engine and public session/player.
- `FatzBombMedia.MediaControl.Wpf`: strict WPF adapter.
- `FatzBombMedia.Player`: validation host.

Future adapters should be separate projects such as `FatzBombMedia.MediaControl.WinUI`, `FatzBombMedia.MediaControl.Avalonia`, and `FatzBombMedia.MediaControl.WinForms`.

## Completed In Stage 10X-0

- Deleted the old broad WPF adapter.
- Renamed the strict adapter to `FatzBombMedia.MediaControl.Wpf`.
- Added Core-owned `MediaControlPlayer`.
- Added WPF `Player : MediaControlPlayer` for adapter-specific conversions without duplicating Core commands.
- Switched Player to the strict WPF adapter.
- Kept media overlay, wash, background, composition, and presentation in Core.
- Fixed video sidecar backgrounds in Core background-video warm-up.
- Removed unused old native-composite remnants.
- Updated boundary check to enforce no WPF UI/bitmap types in Core.

## Remaining 10X Work

- Continue runtime validation from `docs/VALIDATION.md`.
- Ensure WPF `MediaSurface` remains HWND/control interop only.
- Keep WPF UI chrome overlay separate from media overlay.
- Verify primary/secondary output parity for every visual mode.
- Keep adapter scan free of renderer/pipeline/DxCdg/video-frame/D3D11 presentation references.

## Stage 11 Work

- Implement and validate multi-rate scheduling for independent layer cadences.
- Preserve one Core-owned final composition per target.
- Expand adapter project family as needed without moving media/render logic out of Core.
- Add validation automation for primary/secondary target parity and media switch cleanup.

## References

- `docs/CURRENT_ARCHITECTURE.md`
- `docs/STAGE10X_11_TODO.md`
- `docs/VALIDATION.md`
