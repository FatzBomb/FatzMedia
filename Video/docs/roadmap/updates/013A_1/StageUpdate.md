# Stage Update

Stage: 013A.1 - Subtitle ingestion and SRT parsing
Date: 2026-07-10

## Status: COMPLETE

Delivered:

- Added independent subtitle packet fan-out, queue flushing, and EOF tracking to `SharedDemuxer`.
- Added `SubtitleCue` and a pure SRT parser that supports UTF-8 BOM input, CRLF/LF line endings, multiline cues, overlapping cues, comma or period milliseconds, and malformed-cue rejection.
- Added focused parser tests.

Deferred to the next 013A increment:

- Sidecar `.srt` discovery and `SubtitlePipeline` lifecycle/timing.
- Embedded subtitle packet decoding.
- DirectWrite/Direct2D text rasterization and composition overlay integration.

## Verification

- `dotnet test FatzBombMedia.MediaControl.Tests/FatzBombMedia.MediaControl.Tests.csproj --no-restore`
- 190 tests passed.
