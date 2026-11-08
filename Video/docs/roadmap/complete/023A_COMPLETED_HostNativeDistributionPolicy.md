# Completed Stage 023A - Host Native Distribution Policy

Completed: 2026-07-17

## Outcome

Host developers remain responsible for supplying and licensing BASS and FFmpeg.
Managed packages contain no native media binaries.

## Changes

- Documented approved source provenance, verified versions, FFmpeg library majors,
  required/optional filenames, licensing cautions, supported RID, and update process.
- Added public predefined Windows paths for `native` and `runtimes/win-x64/native`;
  caller-supplied paths retain priority.
- Added a binary-free `win-x64` runtime layout and machine-readable native manifest
  to the Windows package.
- Added `scripts/test-windows-native-publish.ps1`, which packs and installs the real
  package graph into an isolated generated host before publishing and loading the
  host-supplied binaries through both custom and predefined paths.

## Verification

- Release build and tests pass.
- Managed packages pack without BASS or FFmpeg DLLs.
- Windows native publish smoke passes using the Player host's local validation set.
- Private-runner CI workflow is prepared without downloading or uploading licensed binaries.
