# Completed Stage — 008A Pure Logic Tests

Status: Complete
Completed: 2026-07-06

## Original Goal

Add automated tests for pure logic that does not require BASS, FFmpeg, GPU, or WPF.

## What Changed

- Created `FatzBombMedia.MediaControl.Tests` test project (xUnit, net10.0-windows7.0, x64) and added it to the solution.
- Added `<InternalsVisibleTo Include="FatzBombMedia.MediaControl.Tests" />` to Core csproj — non-behavioral, does not affect the NuGet package.
- Extracted `NormalizeSeekTarget` from `PlaybackCoordinator` (private) to `SeekHelpers.NormalizeSeekTarget(double, double)` (internal static pure method in new `Pipeline/SeekHelpers.cs`). `PlaybackCoordinator.Seek` delegates to it. No behavior change.
- 108 tests across 7 test classes covering all 5 candidate areas from the stage.

## Files Changed

- `FatzBombMedia.MediaControl.Tests/FatzBombMedia.MediaControl.Tests.csproj` (new)
- `FatzBombMedia.MediaControl.Tests/CdgStateMachineTests.cs` (new)
- `FatzBombMedia.MediaControl.Tests/CdgDirtyRectTests.cs` (new)
- `FatzBombMedia.MediaControl.Tests/FileTypesTests.cs` (new)
- `FatzBombMedia.MediaControl.Tests/MediaStateTests.cs` (new)
- `FatzBombMedia.MediaControl.Tests/AudioPresentationClockTests.cs` (new)
- `FatzBombMedia.MediaControl.Tests/MediaBackgroundResolverTests.cs` (new)
- `FatzBombMedia.MediaControl.Tests/SeekHelpersTests.cs` (new)
- `FatzBombMedia.MediaControl/Pipeline/SeekHelpers.cs` (new)
- `FatzBombMedia.MediaControl/Pipeline/PlaybackCoordinator.cs` (delegated seek normalization to SeekHelpers)
- `FatzBombMedia.MediaControl/FatzBombMedia.MediaControl.csproj` (InternalsVisibleTo)
- `FatzBombMedia.sln` (test project added)

## Validation

- `dotnet build FatzBombMedia.sln` — 0 warnings, 0 errors (Debug)
- `dotnet build FatzBombMedia.sln -c Release` — 0 warnings, 0 errors (Release, TreatWarningsAsErrors=true)
- `dotnet test` — 108 passed, 0 failed
- No production behavior changed; no manual media smoke test required

## Behavior Notes

- Tests target `net10.0-windows7.0` because Core uses `System.Drawing.Color` (Windows-only on .NET 10). No native BASS/FFmpeg/D3D11 DLLs required.
- `FileTypes.Audio` and `FileTypes.Video` sets start empty until Bootstrap runs. Tests for archive/image/cdg sets are fully deterministic. Video sidecar tests temporarily populate `FileTypes.Video` and clear it in `finally`.
- `AudioPresentationClock` running-path tests are limited to deterministic sub-1ms elapsed windows; full rate-smoothing behavior depends on real wall-clock time and is not tested.

## Deferred / Follow-Up Items

- Archive-entry background resolution (`ResolveArchiveBackground`) is not tested beyond verifying the archive service delegate is called. Would need a fake `IArchiveEntryInfo` that writes real bytes through `MediaBackgroundCandidate.FromArchiveEntry`. Low priority.

## Next Suggested Stage

No direct follow-up. The test project and InternalsVisibleTo are in place — additional pure logic tests for future pipeline work can be added incrementally in the same test project.
