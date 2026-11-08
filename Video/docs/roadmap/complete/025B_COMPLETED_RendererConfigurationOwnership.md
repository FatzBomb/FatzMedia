# Completed Stage 025B - Renderer Configuration Ownership

Prepared: 2026-07-18
Completed: 2026-08-03
Status: Complete

## Implementation Status

- Removed the unused Core renderer-selection enum/property and preserved the neutral
  video-decoding constructor.
- Added Direct3D11-owned options with VSync as the default and threaded them through
  both native HWND and DirectComposition presentation paths.
- Added focused tests for default options and every sync-interval override branch.
- Updated the package smoke host to compile against the new public options constructor
  using an isolated NuGet package cache.
- Release build, 328 tests, package/native smoke, and all architecture gates pass.
- Stop-to-play, image/video sidecar policy, and tracker-format seek/loop smoke passed
  across the affected media types.

## Objective

Remove concrete renderer selection and DXGI swap-chain policy from portable Core
configuration. Preserve all current playback, diagnostics, and VSync behavior while
making Direct3D11 options independently configurable through the Direct3D11 factory.

## Current Evidence

- `RenderingMethod` and `MediaPlayerConfiguration.Rendering` have no runtime readers;
  the injected `IMediaPresentationCoordinatorFactory` selects the renderer.
- `NativeSwapChainPresentMode` has one behavioral consumer, the Direct3D11 DXGI
  swap-chain presenter.
- `MediaPlayerConfiguration` still carries renderer-neutral playback, diagnostics,
  timing, audio, subtitle, and karaoke settings used by Core and platform services.

## Contract Decision

- Remove `RenderingMethod` and `MediaPlayerConfiguration.Rendering` from Core.
- Move swap-chain present mode into public `Direct3D11PresentationOptions` in the
  Direct3D11 renderer package.
- Inject Direct3D11 options into `Direct3D11PresentationCoordinatorFactory`; the
  Windows + Direct3D11 convenience factory exposes the same optional configuration.
- Preserve VSync as the default and retain immediate present during live resize or
  explicit synchronization bypass.
- Do not remove unrelated neutral configuration in this stage, even if a setting has
  no current runtime reader.

## Implementation Slices

1. Add Direct3D11-owned options and focused present-policy tests.
2. Thread the options through the D3D11 factory, bridge, presentation services, and
   native/DirectComposition swap-chain backends.
3. Remove the Core renderer enum/property and update the Player host constructor.
4. Update architecture, validation, package API, and stage documentation.
5. Run Release build/tests, package/native smoke, boundary gates, and manual primary/
   secondary presentation checks.

## Acceptance Criteria

- Core contains no DirectX/Direct3D renderer selection enum or DXGI present-mode API.
- Direct3D11 owns and consumes its swap-chain present policy.
- Renderer selection remains explicit through injected factories.
- Default behavior remains VSync; live resize and explicit immediate paths still use
  sync interval zero.
- WPF remains free of renderer references.
- Release build, tests, packages, native smoke, and architecture gates pass.

## Not In Scope

- Adding another renderer or software fallback.
- Removing `VideoDecodingMethod`, presentation timing, or other neutral settings.
- Renaming internal legacy D3D11 namespaces.
- Changing swap-chain creation, resize, composition, or device-loss behavior.
