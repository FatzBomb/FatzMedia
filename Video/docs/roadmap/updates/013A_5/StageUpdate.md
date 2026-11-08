# Stage Update

Stage: 013A.5 - Embedded text tracks, selection, style options, and validation
Date: 2026-07-11

## Status: COMPLETED

Delivered:

- Added embedded text subtitle extraction for ASS/SSA, SubRip, MOV_TEXT, TEXT, and WebVTT streams.
- Exposed available subtitle tracks, selected track ID, and track selection/disable through the public session API.
- Made embedded tracks the default selection when both embedded and sidecar sources exist.
- Added configurable Core subtitle font, size, text color, outline, and shadow settings.
- Added focused parser, selection, style, and embedded timestamp tests.

## Manual Validation

- Player smoke covered sidecar SRT/ASS and embedded ASS playback, selection, subtitle disable, seek, pause/resume, and media switching.
- FFprobe plus runtime diagnostics confirmed `Scorpion King.mkv` player clocks and cue selection matched the embedded ASS packet timestamps. Observed intermittent timing was authored in the source subtitle stream.

## Verification

- `dotnet build -c Release FatzBombMedia.sln --no-restore`
- `dotnet test -c Release FatzBombMedia.MediaControl.Tests/FatzBombMedia.MediaControl.Tests.csproj --no-build`
- 207 tests passed.
