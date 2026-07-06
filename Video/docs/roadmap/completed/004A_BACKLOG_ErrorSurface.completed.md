# Completed Stage — 004A Typed Error Surface

Status: Complete
Completed: 2026-07-06

## Original Goal

Add a host-facing error surface so failures are diagnosable without relying on `false` returns or Debug-only output.

## What Changed

- **Added `MediaErrorCode` enum** (`Api/MediaErrorCode.cs`) with 8 high-value categories: `None`, `Unknown`, `FileNotFound`, `NativeLibraryMissing`, `UnsupportedFormat`, `OpenFailed`, `DecodeFailed`, `InternalError`.
- **Added `MediaErrorEventArgs : EventArgs`** (`Api/MediaErrorEventArgs.cs`) carrying `Code`, `Message`, `SourcePath`, `InnerException`.
- **Added `MediaError` event** (`EventHandler<MediaErrorEventArgs>?`) to `IMediaControlSession` and `MediaControlPlayer` — the public host-facing error event.
- **Added internal `IMediaErrorSource` interface** (`Core/Abstractions/IMediaErrorSource.cs`) with `event Action<MediaErrorInfo>? ErrorOccurred` — the Core-internal seam that keeps pipeline internals out of the public contract.
- **Added internal `MediaErrorInfo` record** (`Core/MediaErrorInfo.cs`) carrying `Code`, `Message`, `SourcePath`, `InnerException`.
- **`PipelineMediaEngine` now implements `IMediaErrorSource`** and raises typed errors at high-value failure points:
  - `Open(string path)`: `FileNotFound` for missing files, `UnsupportedFormat` for unsupported types, `OpenFailed` for coordinator open failures.
  - `Open(MediaInputSource source)`: `UnsupportedFormat` for unknown source kinds.
  - `OpenImageFile`: `FileNotFound`, `OpenFailed` (decoder null), `DecodeFailed` (frame decode null).
- **`MediaControlPlayer` subscribes to `IMediaErrorSource.ErrorOccurred`** and re-marshals to the public `MediaError` event through the existing `MediaControlOperationQueue` (same pattern as `MediaEnded`/`MediaStarted`/`MediaStopped`). Unsubscribes in `Dispose`.
- **Added `MediaErrorEventArgsTests`** (11 tests) covering construction, null handling, and all defined codes.

## Files Changed

**New files:**
- `FatzBombMedia.MediaControl/Api/MediaErrorCode.cs`
- `FatzBombMedia.MediaControl/Api/MediaErrorEventArgs.cs`
- `FatzBombMedia.MediaControl/Core/Abstractions/IMediaErrorSource.cs`
- `FatzBombMedia.MediaControl/Core/MediaErrorInfo.cs`
- `FatzBombMedia.MediaControl.Tests/MediaErrorEventArgsTests.cs`

**Modified files:**
- `FatzBombMedia.MediaControl/Api/IMediaControlSession.cs` — added `MediaError` event
- `FatzBombMedia.MediaControl/Api/MediaControlPlayer.cs` — added `MediaError` event, `OnEngineErrorOccurred` handler, subscribe/unsubscribe in ctor/Dispose
- `FatzBombMedia.MediaControl/Pipeline/PipelineMediaEngine.cs` — implements `IMediaErrorSource`, `RaiseError` helper, raises at all high-value return paths

## Validation

- `dotnet build FatzBombMedia.sln` — 0 warnings, 0 errors (Debug)
- `dotnet build FatzBombMedia.sln -c Release` — 0 warnings, 0 errors (Release, TreatWarningsAsErrors=true)
- `dotnet test` — 119 passed, 0 failed (108 existing + 11 new)
- WPF adapter boundary check: no new Core pipeline/internal/D3D11 namespaces pulled into the adapter. The `MediaError` event is inherited via the public API — no WPF-side changes needed.

## Behavior Notes

- Existing `bool` return values from `Load`/`Play`/`Pause`/`Stop`/`Seek` are preserved — the `MediaError` event is additive, not a replacement.
- The `MediaError` event fires from background threads (operation queue); hosts must marshal to the UI thread, consistent with the existing `MediaEnded`/`MediaStarted`/`MediaStopped` events.
- No per-frame or per-decode raise points were added (stage explicitly forbids noisy logging).

## Deferred / Follow-Up Items

- **Bootstrap/native-library-load failures** still surface only as `InvalidOperationException` from the constructor (no `MediaControlPlayer` exists yet to raise `MediaError`). Acceptable since the exception already carries `Bootstrap.Message`.
- **Internal pipeline `catch` blocks** in `PlaybackScheduler`, `CdgPipeline`, `VideoPipeline` (seek workers, loop restarts, DWM timing) still swallow exceptions via `Debug.WriteLine` only. Lower value, out of scope for the first safe step.
- **`AudioEngine` BASS init failure and normalization failure** are still `Debug.WriteLine`-only.
- **Device/scheduler loop failures** not surfaced (stage explicitly forbids device recovery).
- **Targeted open/failure smoke tests** (load missing file, load unsupported format) require manual verification via the Player host.
