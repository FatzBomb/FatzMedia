<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: COMPLETED.

# Completed Stage 016A — ClockAudioEngine / Clock Source Abstraction Split

## Status

Completed 2026-07-10. Audio latency feedback and state ownership were stable before implementation.

## Why This Exists

`ClockAudioEngine` had been used for standalone CDG and video-without-audio despite implementing audio-only operations that cannot apply to a virtual clock. The split makes the clock-backed transport explicit while preserving the existing public non-null `Audio` API.

## Preconditions

- Audio latency feedback (006A) — done 2026-07-07
- State ownership (003B-003E) — done 2026-07-05

## Delivered

- Added the internal `IClockSource` transport contract.
- Moved `VideoPipeline`, `CdgPipeline`, and `MediaEffects` to use the clock contract directly for no-audio timing and tempo.
- Retained `ClockAudioEngine` only as a compatibility adapter for existing `Audio` consumers.
- Added focused `PlaybackClock` contract tests.

## Preserved Behavior

- Standalone CDG tempo
- Video without audio tempo
- Real BASS tempo for audio-backed media
- Position/duration reporting

## Verification

- `dotnet test FatzBombMedia.MediaControl.Tests/FatzBombMedia.MediaControl.Tests.csproj --no-restore`
- 186 tests passed.
