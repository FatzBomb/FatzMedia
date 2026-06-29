# Stage 10X / 11 TODO

## Stage 10X Goal

Finish boundary hardening and activate the control-owned multi-swap-chain compositor work without reintroducing adapter rendering.

## Stage 10X Current State

- Old broad WPF adapter project was deleted.
- Active WPF adapter is `FatzBombMedia.MediaControl.Wpf`.
- Player references the strict adapter only.
- Core owns sidecar image/video backgrounds and the final D3D11 compositor path.
- WPF adapter contains no renderer, pipeline, DxCdg, video-frame, or D3D11 presentation references.
- Video sidecar backgrounds are fixed through Core background-video warm-up.

## Stage 10X Remaining Work

- Validate primary output:
  - audio-only clear
  - image-only
  - video-only hardware/software
  - audio+DxCdg
  - audio+DxCdg with sidecar image background
  - audio+DxCdg with sidecar video background
- Validate secondary/projector output receives the same final Core-composed frame.
- Validate seek while paused/playing for video, image, DxCdg, and sidecar backgrounds.
- Validate resize/fullscreen and WPF UI chrome overlay without media-layer involvement.
- Keep adapter boundary scans clean.
- Remove or rename any remaining stale renderer terminology in active code/docs.

## Stage 10X Guardrails

- Do not add renderer/pipeline/layer/timing logic to adapters.
- Do not add WPF/WinUI/Avalonia/WinForms types to Core.
- Do not create media-type-specific final presentation paths.
- Fix missing behavior in Core after verifying Core does not already own it.

## Stage 11 Direction

- Formalize multi-rate/multi-swap-chain scheduling for layers with different cadences.
- Keep the output contract as a single Core-owned final render per presentation target.
- Add adapter projects only as framework interop packages.
- Consider adapter-specific `Player` facades that inherit Core `MediaControlPlayer` and add only framework conversion overloads.
- Expand validation automation around primary/secondary target parity.
