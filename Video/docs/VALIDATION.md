# Validation Checklist

## Build And Boundary

- `dotnet restore FatzBombMedia.sln`
- `dotnet build FatzBombMedia.sln --no-restore`
- `./scripts/check-wpf-boundary.ps1`
- `./scripts/check-presentation-parity.ps1`
- The boundary script also scans the WPF adapter for renderer/pipeline/Cdg/video-frame/D3D11 presentation references.

## Player Smoke Tests

1. Audio file plays.
2. Volume, pitch, tempo, and normalization work.
3. MP3+CD+G renders Cdg lyrics.
4. ZIP audio+CD+G renders Cdg lyrics.
5. Video plays.
6. Video tempo keeps audio/video sync.
7. Image-only renders as a still visual.
8. Audio+Cdg sidecar image background appears behind lyrics.
9. Audio+Cdg sidecar video background appears behind lyrics.
10. Seek works for audio, Cdg, video, and sidecar background cases.
11. Primary native HWND output resizes cleanly.
12. Fullscreen WPF UI chrome overlay appears above the native HWND without becoming a media layer.
13. Secondary/projector output receives the same final Core-composed frame.
14. Closing secondary output while playing does not disturb primary output.
15. Repeated media-type switches leave no stale layer artifacts.

## Automated Architecture Checks

- `check-wpf-boundary.ps1` verifies Core does not reference WPF UI/bitmap types and the WPF adapter does not reference renderer/pipeline/Cdg/D3D11 presentation code.
- `check-presentation-parity.ps1` verifies the source-level primary/secondary output contract: WPF primary and secondary surfaces both attach through neutral `MediaPresentationSurfaceDescriptor.NativeHwnd`, Core retains attached surfaces in one registry, and the D3D11 compositor captures/draws/presents all active final targets through one target plan.
