# FatzBomb.MediaControl.Rendering.Direct3D11

Direct3D 11 renderer backend and subtitle rasterizer for `FatzBomb.MediaControl`.

The host composition root selects this renderer explicitly. Core owns presentation
policy through `MediaPresentationCoordinator`; this package executes native-frame,
composition, swap-chain, DirectComposition, and final-present operations through
`IMediaRendererBackend`.

Renderer-specific creation settings live in this package rather than Core. Supply
`Direct3D11PresentationOptions` to `Direct3D11PresentationCoordinatorFactory` when
the host needs a non-default DXGI swap-chain present mode. VSync remains the default.

Device selection (Stage 036A): `DeviceSelection = Auto` (the default) creates the device on
the GPU and falls back to WARP, Windows' built-in multi-core software rasterizer, when no GPU
device can be created; `Hardware` and `Warp` force one. WARP keeps the native swap-chain
path. Each device creation logs which device, adapter and feature level it got.
`Direct3D11PresentationCoordinatorFactory` implements `IMediaPresentationAvailabilityProbe`,
so a host can put it first in a `FallbackPresentationCoordinatorFactory` (for example before
the software renderer); on a software rasterizer the probe asks for software decoding.
