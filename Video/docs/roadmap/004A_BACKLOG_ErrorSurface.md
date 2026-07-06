<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: BACKLOG.

# Stage 004A — Typed Error Surface


## Status

Backlog. Promote only after Stage 003B is complete.

## Goal

Add a host-facing error surface so failures are diagnosable without relying on `false` returns or Debug-only output.

## Why This Matters

The engine currently has strong media-path behavior, but production hosts need to distinguish missing files, missing native DLLs, corrupt media, unsupported formats, FFmpeg/BASS failures, background media failures, device problems, and scheduler loop failures.

## Intended Direction

- Define a small typed error/event/result model.
- Surface high-value failures without spamming per-frame paths.
- Preserve existing public behavior unless the stage explicitly chooses an API addition.

## Not Allowed

- Do not rewrite media loading.
- Do not change decoder selection.
- Do not add noisy per-frame logging.
- Do not implement device recovery in this stage.

## First Safe Step

Map current failure return paths and exception swallow/log-only paths, then add the smallest host-facing error event/result path.

## Validation

Public/API/media behavior change. Use Debug + Release + boundary/parity scripts, then targeted open/failure smoke tests.
