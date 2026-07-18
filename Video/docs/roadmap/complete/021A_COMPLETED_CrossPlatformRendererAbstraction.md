# Completed Stage 021A - Cross-Platform Renderer Abstraction

Completed: 2026-07-17

## Outcome

Core now owns the portable media and presentation policy needed by future platform
and renderer packages. The current Direct3D11 path remains the Player host default,
but neither Windows services nor the WPF adapter depends on that renderer.

## Changes

- Replaced D3D11-named frame and composition fields with producer-owned native video resource descriptors.
- Moved FFmpeg hardware-device preference and native-frame extraction behind `IVideoHardwareAccelerationServices`; Windows supplies the current implementation.
- Moved presentation state, engine attachment, pacing, composition-frame construction, retained background policy, multi-surface lifecycle, and recovery orchestration into Core `MediaPresentationCoordinator`.
- Added public `IMediaRendererBackend` and `ISubtitleRasterizer` contracts; Direct3D11 implements GPU execution and DirectWrite/Direct2D rasterization.
- Moved renderer-independent layer schedules, update plans, target state, and target lifecycle planning into Core.
- Removed production `InternalsVisibleTo` grants between Core, Direct3D11, and Windows.
- Removed the Windows-to-Direct3D11 project reference. The optional `FatzBomb.MediaControl.Windows.Direct3D11` integration package owns the default pairing; WPF references Windows only.

## Verification

- Core targets portable `net10.0` and has no platform, renderer, UI, P/Invoke, or Vortice dependency.
- Windows references Core only.
- WPF references Windows only and contains no renderer/pipeline/internal-CDG/D3D11 references.
- Direct3D11 consumes only public Core contracts.
- Release build, full tests, Core package, and architecture boundary scripts pass.
- Player visual smoke validation passed on 2026-07-18 with no reported regressions.
