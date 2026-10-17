# Stage Update

Stage: 034A.1 - Native dependency report
Date: 2026-10-17

## Status: IMPLEMENTED - manual Player smoke pending

Delivered: see the stage file. Also a "Shipping FFmpeg With Your App" checklist and a
"Startup Dependency Report" section in `docs/NATIVE_DEPENDENCIES.md`.

Tests: 11 focused cases in `NativeDependencyReportTests.cs` (Portable) and 3 in
`LinuxNativeLibraryBootstrapperTests` (Linux).

## Decisions made without the owner

- The report is a property, not an event: the platform loads native libraries inside the
  player's constructor, before a host could subscribe, so a startup event would never
  be seen.
- The new interface member has a default implementation, so third-party platform
  factories keep compiling and report nothing.
- Missing optional BASS plugins are listed as optional problems, not errors. Since 033A
  slice 2, FFmpeg still plays most of their formats.

## Verification

- Linux: Release build clean; the new Portable and Linux tests pass.
- Windows bootstrap changes compile here but run only on Windows.

## Manual smoke (Windows, native DLLs)

1. Normal start with every DLL present: the Player's Log panel shows no
   `NativeLibraries` lines (or only warnings for plugins you do not ship, such as
   `bassalac.dll`).
2. Copy the `native` folder somewhere safe, then replace `avcodec-63.dll` with an FFmpeg 8
   `avcodec-62.dll`: the Player reports "FFmpeg avcodec-63.dll (required) is missing.
   Found avcodec-62.dll, a different version; this control needs avcodec-63.dll." Restore
   the folder.
3. Remove `bassopus.dll`: a `NativeLibraries` warning names it, and `.opus` files still
   play through the FFmpeg fallback (033A slice 2).

## Review follow-up (same day)

- On Windows, a complete set of FFmpeg DLLs that fails to load (mixed builds, wrong
  architecture, or a missing dependency) made FFmpeg.AutoGen throw instead of returning
  null. That skipped the report and left it empty. The bootstrap now catches it, marks the
  DLLs `FailedToLoad` with the loader's message, and always records the report.
- Known limit: a DLL of the wrong architecture is skipped by the locator, so it shows as
  missing without a hint.

