# Completed Stage 025A - Presentation Surface Contract Neutrality

Prepared: 2026-07-18
Completed: 2026-07-18
Status: Complete

## Implementation Status

- Added extensible native surface type and handle IDs plus immutable borrowed handle payloads.
- Removed `MediaPresentationSurfaceKind` and the descriptor's top-level `NativeHandle`.
- Core validates only neutral descriptor invariants.
- Direct3D11 validates `win32-hwnd` and extracts the named `window` handle.
- WPF primary and secondary surfaces create neutral Win32 payloads.
- Focused tests and presentation/boundary gates pass.
- Primary/secondary attach, resize, fullscreen, close, reopen, and stop-to-play visual smoke passed.

## Objective

Replace the closed `MediaPresentationSurfaceKind.NativeHwnd` plus single
`MediaPresentationSurfaceDescriptor.NativeHandle` contract with an extensible,
renderer-neutral native-surface payload. Preserve current WPF HWND and Direct3D11
behavior exactly while allowing future X11, Wayland, AppKit, and Android surface
descriptors without changing Core session/coordinator APIs again.

## Current Evidence

- `FatzBomb.MediaControl/Api/MediaPresentationSurfaceDescriptor.cs` stores one
  `IntPtr NativeHandle` and validates only `NativeHwnd`.
- `FatzBomb.MediaControl/Api/MediaPresentationSurfaceKind.cs` is a closed enum with
  only `None` and `NativeHwnd`.
- `MediaPresentationCoordinator.AttachPresentationSurface` rejects every kind except
  `NativeHwnd`, making Core responsible for a Windows-specific decision.
- `NativeD3D11PresentationBridge` needs an HWND but can validate/extract it at the
  renderer boundary instead of requiring Core to understand it.
- WPF creates HWND descriptors in only `MediaSurface` and
  `SecondaryPresentationWindow`; these are the adapter migration points.

## Contract Decision

Use immutable, borrowed native-surface metadata rather than platform classes or an
opaque `object`:

```text
MediaPresentationSurfaceDescriptor
  Id, Role, PixelWidth, PixelHeight, DpiScaleX, DpiScaleY
  NativeSurface

MediaNativeSurfaceDescriptor
  SurfaceTypeId                 # extensible stable value, not a closed enum
  Handles[]                     # named nint values

MediaNativeHandle
  HandleId
  Value
```

Core will define stable identifier value types and known IDs for interoperability,
including `win32-hwnd` and `window`. The contract remains open to additional IDs such
as `x11-window` (`display`, `window`), `wayland-surface` (`display`, `surface`),
`appkit-view` (`view`), and `android-native-window` (`window`) without adding enum
members or changing constructor shape.

Native handles are borrowed. The adapter/platform owns their lifetime; Core and the
renderer must not close, release, retain, or destroy them. They remain valid from a
successful attach until detach or replacement for the same surface ID.

## Implementation Slices

1. Add the neutral native-surface/handle value objects and validation tests in Core.
2. Change `MediaPresentationSurfaceDescriptor` to carry the native-surface payload;
   preserve ID, role, size, DPI, and `WithSize` behavior.
3. Remove Core coordinator checks for a specific platform surface type; validate only
   neutral descriptor invariants before forwarding to `IMediaRendererBackend`.
4. Update Direct3D11 backend attach/resize to require `win32-hwnd`, extract the named
   `window` handle, and reject unsupported payloads with no fallback or ownership change.
5. Update WPF primary and secondary adapters to create the neutral Win32 payload.
6. Update presentation-parity and boundary scripts, architecture docs, API comments,
   and focused unit tests.
7. Run Release build/tests, package checks, native package smoke, and the manual visual
   subset for primary/secondary attach, resize, fullscreen, close, and reopen.

## Expected Files

- `FatzBomb.MediaControl/Api/MediaPresentationSurfaceDescriptor.cs`
- `FatzBomb.MediaControl/Api/MediaPresentationSurfaceKind.cs` (remove after migration)
- New Core native-surface identifier/handle contract files under `Api/`
- `FatzBomb.MediaControl/Controllers/MediaPresentationCoordinator.cs`
- `FatzBomb.MediaControl.Rendering.Direct3D11/NativeD3D11PresentationBridge.cs`
- `FatzBomb.MediaControl.Wpf/MediaSurface.cs`
- `FatzBomb.MediaControl.Wpf/SecondaryPresentationWindow.cs`
- Focused tests in `FatzBomb.MediaControl.Tests`
- `scripts/check-presentation-parity.ps1`
- `docs/CURRENT_ARCHITECTURE.md`, `docs/VALIDATION.md`, and stage records

## Acceptance Criteria

- Core public surface descriptors expose no closed platform-surface enum and no
  top-level single native handle.
- Core presentation coordination contains no HWND/Win32 surface-kind branch.
- Direct3D11 alone validates and consumes the Win32 HWND payload.
- WPF remains free of renderer, pipeline, internal-CDG, Vortice, and D3D11 references.
- Borrowed ownership and validity rules are documented and unit tested.
- Primary/secondary lifecycle and multi-target parity remain unchanged.
- Release build has zero warnings; all tests, packages, boundary gates, and native
  package smoke pass.

## Risks

- A loose string dictionary would permit silent handle-name typos; use typed string
  identifier value objects and reject duplicate/zero required handles.
- Do not use `object`, reflection, or platform-specific subclasses in Core.
- Do not change HWND creation, swap-chain ownership, resize timing, or detach ordering.
- Do not retain adapter-owned handles past detach.

## Not In Scope

- Implementing X11, Wayland, AppKit, Android, Vulkan, Metal, or OpenGL.
- Creating a software renderer.
- Changing presentation timing or composition policy.
- Removing `RenderingMethod` or moving `NativeSwapChainPresentMode`.

## Follow-Up Stage 025B

After 025A is validated, prepare renderer configuration ownership:

- Remove `RenderingMethod` and `MediaPlayerConfiguration.Rendering`; the injected
  renderer/platform factory already selects the backend.
- Move `NativeSwapChainPresentMode` from Core configuration into Direct3D11-specific
  factory/options configuration.
- Preserve renderer-neutral playback and subtitle style settings in Core.
