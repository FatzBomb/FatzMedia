# Active Stage 034A - Native Dependency Report

Prepared: 2026-10-17
Status: Slice 1 implemented; owner smoke passed 2026-10-17 (`updates/034A_1`). Owner request
(2026-10-17): report a missing or wrong-version native file clearly at startup. An
automatic downloader was discussed and dropped: hosts can ship LGPL FFmpeg DLLs with
their installer and updates (see "Shipping FFmpeg With Your App" in
`docs/NATIVE_DEPENDENCIES.md`).

## Why This Is Worth Doing

When a DLL was missing, the platform logged it and video or a format just failed to
open. After the FFmpeg 9 upgrade, an install with FFmpeg 8 DLLs fails the same way. A host
needs the exact file name, folder, and cause to tell the user what to fix.

## Slice 1 Acceptance Criteria

- Public `NativeDependencyReport`, `NativeLibraryCheck`, and `NativeLibraryStatus`
  (`Loaded`, `Missing`, `NotLoaded`, `FailedToLoad`), exposed as
  `MediaControlPlayer.NativeDependencies` and as a default member of
  `IMediaControlPlatformFactory` (existing implementations keep compiling and return
  `Empty`).
- Windows and Linux bootstraps record one check per BASS and FFmpeg library, including
  optional plugins; a retry after a failed start replaces that component's earlier checks.
- A missing versioned file (`avcodec-63.dll`, `libavcodec.so.63`) found in another version
  in a search path gets a hint, which also goes into `LastNativeLoadMessage`.
- FFmpeg files split across folders are reported as `NotLoaded`, not missing.
- The Player test app logs `Describe()` at startup, also when startup fails (read from the
  platform factory, since the player constructor throws): required problems as errors,
  optional ones as warnings.
- Focused tests: report logic (Portable) and the Linux bootstrapper's report (fake
  native operations). The Windows bootstrap is covered by the manual smoke.

## Explicit Non-Goals

- Downloading or updating native files. Hosts ship them.
- A UI in the control. Hosts decide how to show the report.
