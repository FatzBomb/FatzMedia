# Stage Update

Stage: 019A.1 - Video frame lifetime matrix, contract, conformance tests
Date: 2026-10-16

## Status: IMPLEMENTED - pending manual regression smoke

Delivered: producer/consumer matrices and the API-impact evaluation in the stage file; the
ephemeral-by-default contract in `VideoFrameEventArgs` XML docs; 4 conformance tests
(`VideoFrameLifetimeTests.cs`). `TestMediaEngine.ImageFrameReady` became a real event
(`RaiseImageFrame`). No production behaviour changed.

## Decisions made without the owner

- Kept `VideoFrameEventArgs` and `PixelDataCanBeRetained` instead of introducing a lease
  type or reusing instances: no consumer retains frames, hosts never see the event, and
  the measurable win (~80 B/frame) does not justify a public lifetime change.
- A lease type is recorded as the prerequisite for removing 010A copy 3, if that is ever
  pursued.

## Not verified here

- That Direct3D11's video-processor background path finishes with the decoder texture
  inside the renderer call (by code reading only; Windows hardware decode required).

## Manual smoke (Windows, native DLLs)

1. Hardware-decoded video (default) and `--software`: plays, pauses, seeks, and loops
   (loop-start frame shows without a flash).
2. Audio file with a video sidecar background, with hardware decode on and off.
3. Image file, then image background behind CDG: both display and survive a resize.
