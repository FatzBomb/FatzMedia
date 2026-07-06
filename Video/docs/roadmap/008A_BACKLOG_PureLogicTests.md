<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: BACKLOG.

# Stage 008A — Pure Logic Tests


## Status

Backlog. Can be promoted earlier if desired because it should not disturb runtime behavior.

## Goal

Add automated tests for pure logic that does not require BASS, FFmpeg, GPU, or WPF.

## Why This Matters

The project currently relies heavily on build checks, scripts, and manual smoke testing. Pure logic tests can catch regressions cheaply.

## Candidate Test Areas

- CDG state machine behavior.
- Sidecar/background resolver rules.
- `AudioPresentationClock` math.
- Seek acceptance and timing helper logic.
- Media state transition rules after Stage 003B.

## Not Allowed

- Do not mock the entire media engine just to claim coverage.
- Do not block productive work on a huge test framework.
- Do not require native DLLs/GPU in the initial test pass.

## First Safe Step

Create the smallest test project and add tests for one pure component with no runtime/native dependencies.

## Validation

Build plus test run. No manual media smoke unless production source behavior is changed.
