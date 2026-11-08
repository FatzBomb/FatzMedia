# Stage Update

Stage: 013A.4 - Text rasterization and composition integration
Date: 2026-07-10

## Status: IMPLEMENTED - manual playback validation pending

Delivered:

- Added `Vortice.Direct2D1` 3.8.3 for Core-owned DirectWrite, Direct2D, and WIC interop.
- Added `SubtitleTextRenderer`, which creates transparent BGRA text canvases without WPF rendering APIs.
- Added a separate retained subtitle layer to `MediaCompositionFrame` and the D3D11 composition presenter.
- Routed hardware-decoded primary video through the compositor when subtitles are active, preserving subtitle visibility on both software and hardware paths.
- Kept the CDG indexed/palette overlay path independent from subtitle BGRA data.
- Added native text-rasterization and composition-frame layer tests.

Remaining validation:

- Manual Player smoke with SRT and ASS sidecars across software and hardware video, seek, pause/resume, and media switching.
- Embedded subtitle stream decoding remains outside the sidecar text-subtitle MVP.

## Verification

- `dotnet test FatzBombMedia.MediaControl.Tests/FatzBombMedia.MediaControl.Tests.csproj --no-restore`
- 201 tests passed.
