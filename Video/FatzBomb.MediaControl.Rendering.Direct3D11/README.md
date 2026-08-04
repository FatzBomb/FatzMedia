# FatzBomb.MediaControl.Rendering.Direct3D11

Direct3D 11 renderer backend and subtitle rasterizer for `FatzBomb.MediaControl`.

The host composition root selects this renderer explicitly. Core owns presentation
policy through `MediaPresentationCoordinator`; this package executes native-frame,
composition, swap-chain, DirectComposition, and final-present operations through
`IMediaRendererBackend`.

Renderer-specific creation settings live in this package rather than Core. Supply
`Direct3D11PresentationOptions` to `Direct3D11PresentationCoordinatorFactory` when
the host needs a non-default DXGI swap-chain present mode. VSync remains the default.
