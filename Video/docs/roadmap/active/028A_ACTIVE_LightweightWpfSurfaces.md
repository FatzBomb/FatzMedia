# Active Stage 028A - Lightweight WPF Surfaces and Composited Presentation

Prepared: 2026-08-06
Status: Implementation and automated validation complete; manual native smoke pending

## Objective

Provide self-contained WPF background and karaoke controls while preserving the
performance-first native HWND path and adding an explicit WPF-composited alternative
for layouts that cannot accept HWND airspace limitations.

## Delivered

- Renamed the WPF-only mode contract to `WpfPresentationMode`.
- Kept `NativeHwndOverlay` as the default and added opt-in `WpfComposited`.
- Added a renderer-neutral managed BGRA frame-sink contract.
- Added a Direct3D11 offscreen target with final-frame GPU readback for WPF composition.
- Extended regular `MediaSurface` with pre-load mode selection and in-tree overlays.
- Added design-mode suppression so the XAML designer creates no native surface or session.
- Added self-contained `BackgroundSurface` and `KaraokeSurface` controls to the WPF
  package; hosts inject `IMediaControlPlatformFactory` before loading media.
- Kept shared player/session lifecycle in the internal composed
  `LightweightPlayerController`; no public base control is exposed.
- Added a Player-host presentation-mode selector for validation.

## BackgroundSurface Contract

- Supports opaque color, image, and silent looping video backgrounds.
- `AutoPlay` defaults to `true`.
- Video audio is always muted and sidecar discovery is disabled.
- Arbitrary `OverlayContent` is routed through an overlay window in HWND mode or the
  normal WPF visual tree in composited mode.

## KaraokeSurface Contract

- Supports CDG, audio+CDG, archive karaoke, and MIDI/KAR lyrics.
- Rejects video and non-karaoke audio after loading without auto-starting it.
- Uses an opaque configurable canvas, black by default.
- Arranges the presentation surface at 4:3 and exposes no video/background feature set.
- `AutoPlay` defaults to `true`.

## Explicit Non-Goals

- Mixed HWND and WPF-composited targets in one player session.
- Switching presentation mode while media is open.
- Making composited readback the default or claiming native-HWND-equivalent frame rate.
- Moving D3D11, composition, CDG, subtitle, or pipeline logic into the base WPF adapter.
- Requiring composited presentation in UI frameworks without WPF-style airspace limits.

## Remaining Manual Validation

1. Regular `MediaSurface` video, CDG, and MIDI/KAR in both modes.
2. WPF clipping, transforms, opacity, and interactive overlay content in composited mode.
3. BackgroundSurface color, image, muted looping video, unload/reload, and both modes.
4. KaraokeSurface CDG and MIDI/KAR, 4:3 resizing, opaque canvas, and both modes.
5. Rejection of video/non-karaoke input without audible or visible playback.
6. Native-mode primary/secondary output regression smoke.

## Automated Validation

- Debug and Release solution builds: pass, zero warnings.
- 348 tests: pass.
- Core, WPF, and presentation-parity architecture gates: pass.
- Managed frame-sink handle creation, resolution, and disposal tests added.
