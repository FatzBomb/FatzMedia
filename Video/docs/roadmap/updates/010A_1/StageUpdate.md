# Stage Update

Stage: 010A.1 - Copy map, benchmark, shared retained background
Date: 2026-10-16

## Status: IMPLEMENTED - pending manual smoke

Delivered: see the stage file. New `Controllers/RetainedFrameBuffer.cs`;
`MediaPresentationCoordinator` shares the retained buffer with compositions; benchmark in
`FatzBomb.MediaControl.Linux.Tests/SoftwareVideoCopyBenchmark.cs`; 5 new tests
(`RetainedFrameBufferTests.cs`) including a reentrant "new frame during present" test that
proves no tearing and a test that both presents use the retained array (no snapshot copy).
`TestMediaEngine.BackgroundFrameReady` became a real event for these tests.

## Effect

Removes one full-frame memcpy per composed software frame: ~0.7 ms at 1080p and ~3 ms at
2160p on this machine (comparable to the decode+convert time itself).

## Decisions made without the owner

- Kept copy 3 (decoder pool ownership hand-off is a larger change across VideoPipeline).
- Reused the existing composition pool and its 64 MB bucket limit.

## Manual smoke (Windows, native DLLs)

1. Audio file with a video sidecar background, `--software` and default renderer: the
   background video plays smoothly; no flicker or torn frames; memory stays flat over
   several minutes (Task Manager).
2. Image background with karaoke/CDG overlay: overlay updates; background stable.
3. Switch tracks rapidly 10 times: no growth in private bytes after settling.
