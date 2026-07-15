# FatzBomb.MediaControl.Rendering.Direct3D11

Direct3D 11 renderer and native presentation backend for `FatzBomb.MediaControl`.

Windows platform composition selects this renderer by default. Other renderer
packages can implement `IMediaPresentationCoordinatorFactory` without changing
Core playback orchestration or UI adapters.
