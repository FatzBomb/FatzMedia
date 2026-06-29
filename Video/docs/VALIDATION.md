# Validation Checklist

## Build And Boundary

- `dotnet restore FatzBombMedia.sln`
- `dotnet build FatzBombMedia.sln --no-restore`
- `./scripts/check-wpf-boundary.ps1`
- Scan adapter project for renderer/pipeline/DxCdg/video-frame/D3D11 presentation references.

## Player Smoke Tests

1. Audio file plays.
2. Volume, pitch, tempo, and normalization work.
3. MP3+CD+G renders DxCdg lyrics.
4. ZIP audio+CD+G renders DxCdg lyrics.
5. Video plays.
6. Video tempo keeps audio/video sync.
7. Image-only renders as a still visual.
8. Audio+DxCdg sidecar image background appears behind lyrics.
9. Audio+DxCdg sidecar video background appears behind lyrics.
10. Seek works for audio, DxCdg, video, and sidecar background cases.
11. Primary native HWND output resizes cleanly.
12. Fullscreen WPF UI chrome overlay appears above the native HWND without becoming a media layer.
13. Secondary/projector output receives the same final Core-composed frame.
14. Closing secondary output while playing does not disturb primary output.
15. Repeated media-type switches leave no stale layer artifacts.
