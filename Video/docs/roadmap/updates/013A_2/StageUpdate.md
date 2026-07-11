# Stage Update

Stage: 013A.2 - Sidecar SRT lifecycle
Date: 2026-07-10

## Status: COMPLETE

Delivered:

- Added `.srt` recognition and video-sidecar discovery.
- Added `MediaContent.Subtitle`, `ISubtitleEngine`, and a thread-safe `SubtitleEngine` that returns active overlapping cues for a playback position.
- Added `SubtitlePipeline` ownership to `MediaSession` and `PlaybackCoordinator`.
- Added video-plus-sidecar opening through `PipelineMediaEngine` and exposed `HasSubtitle` / `Subtitle` on the Core public session API.
- Added focused subtitle-engine and file-type tests.

Deferred to the next 013A increment:

- DirectWrite/Direct2D text rasterization.
- Subtitle BGRA overlay composition and native presentation.
- Embedded subtitle stream decoding.

## Verification

- `dotnet test FatzBombMedia.MediaControl.Tests/FatzBombMedia.MediaControl.Tests.csproj --no-restore`
- 196 tests passed.
