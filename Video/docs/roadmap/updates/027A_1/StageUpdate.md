# Stage Update

Stage: 027A.1 - Avalonia adapter (composited frame-sink surface)
Date: 2026-10-16

## Status: IMPLEMENTED - headless-validated; manual desktop smoke pending

Delivered:

- `FatzBomb.MediaControl.Avalonia`: `MediaSurface` (Player, VideoDisplayMode,
  Background; attach/resize/detach lifecycle; DPI via `TopLevel.RenderScaling`),
  internal `AvaloniaFrameSink` (background-thread frames -> UI-thread
  `WriteableBitmap`, latest wins), `AvaloniaMediaInputSources.FromBitmap`.
- `FatzBomb.MediaControl.Avalonia.Tests` (xunit v3 + Avalonia.Headless + Skia): 13
  cases including a rendered-frame capture proving a background-thread frame is drawn.
- `scripts/check-adapter-boundary.ps1` (new CI step) applies the WPF adapter rules to
  Avalonia; a single-line-file blind spot (`Get-Content` returning a string) was fixed in
  the new script and in the existing Core/WPF boundary scripts.
- Linux CI runs the Avalonia headless tests.

## Decisions made without the owner

- Avalonia 12.1.4 (current stable); test project uses xunit v3 because
  `Avalonia.Headless.XUnit` 12 requires it.
- Composited (frame-sink) mode first: it works on Windows and Linux today; native
  swapchain mode waits for the Vulkan swapchain slice.
- Default `VideoDisplayMode.Zoom`, matching the WPF `MediaSurface`.

## Verification

- Linux: Release build clean; Avalonia tests 13/13; all four boundary scripts pass
  locally via the PowerShell .NET tool (with negative probes confirming detection).

## Manual smoke (desktop)

1. Windows: Avalonia app with `MediaSurface Player="{Binding Player}"`, player from
   `new MediaControlPlayer(new WindowsMediaControlPlatformFactory(new
   SoftwarePresentationCoordinatorFactory()), ...)`: video plays, window resize and
   200 % DPI keep sharp output, closing the window detaches cleanly.
2. Linux (with BASS + FFmpeg): same with `LinuxMediaControlPlatformFactory`.
3. Switch `Player` at runtime between two sessions: the old one stops receiving frames.
