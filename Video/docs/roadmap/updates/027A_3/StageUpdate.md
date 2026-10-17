# Stage Update

Stage: 027A.3 - WinForms adapter (HWND MediaSurface)
Date: 2026-10-16

## Status: IMPLEMENTED - Windows CI tests; manual Windows smoke pending

Delivered:

- `FatzBomb.MediaControl.WinForms` (net10.0-windows, x64, Core only): `MediaSurface :
  Control` attaches its HWND as the primary `win32-hwnd` surface on handle creation or
  `Player` change, resizes on client-size/DPI change, detaches on handle destruction,
  dispose, or `Player` change; opaque painting so the renderer owns the pixels.
- 3 STA tests in the Windows test project (`WinFormsMediaSurfaceTests`, excluded from
  the portable link) using a `DispatchProxy` session recorder.
- Boundary: added to the reference DAG and to `check-adapter-boundary.ps1`.

## Decisions made without the owner

- Slice 3 (WinForms) done before slice 2 (Avalonia native mode), which depends on the
  Vulkan swapchain slice.
- The control assumes a DPI-aware WinForms process (`ClientSize` in device pixels).

## Verification

- Linux: full solution build with Windows targeting clean; format clean; all boundary
  scripts pass. The tests need the WindowsDesktop runtime and run on Windows CI only.

## Manual smoke (Windows, native DLLs)

1. A WinForms app with `ApplicationHighDpiMode.PerMonitorV2`, a docked `MediaSurface`,
   and `new MediaControlPlayer(new WindowsDirect3D11MediaControlPlatformFactory(), ...)`:
   video and CDG play; maximize/restore and moving between monitors of different DPI keep
   the output sharp; closing the form detaches without errors.
