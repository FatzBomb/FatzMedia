# ENG-039 batch 4b: Core seams and the ENG-037 run-time fallback (R2-R4, G3x)

2026-10-28. Branch `claude/festive-newton-yvvib5-b4b` (on top of 4a). Status: implemented,
pending manual smoke.

## What changed

- **R2, decode-device seam.** An internal `IRendererVideoDecodeAttachment` (attach, detach),
  implemented by `PipelineMediaEngine` and `TestMediaEngine`; `MediaPresentationCoordinator` uses
  it instead of casting to `PipelineMediaEngine`. `VideoDecoder`, `VideoPipeline`, its factory
  and the engine take `RendererVideoDecodeServices` directly (no downcasts).
- **R3, one decoder replace.** `VideoPipeline.TryReplaceDecoder` (dispose, create, open, adopt
  the timeline origin, duration, reposition) serves the network/device swap and the startup
  software fallback, with each caller's behaviour unchanged.
- **R4, one teardown.** `MediaControlPlayer.FreeSessionGraph` frees the session graph for Reset,
  mode switches and Dispose, and unsubscribes every handler (including batch 2's
  `DeviceRecovered` reopen) in one place. Order unchanged.
- **G3x, ENG-037 at run time.** When the renderer refuses a hardware frame
  (`EnsureNativeFrameDevice` false: WARP, a failed device adoption), the coordinator requests
  software decoding through a per-decoder token, `NativeVideoFrameResource.DecodeFallback`
  (additive public type `VideoDecodeFallback`, like `HdrToneMapFallback`). Not while the
  coordinator is disposed, recovering a lost device, or without a surface. The pipeline queues
  the request and swaps to software at the current position and transport state on its
  recovery worker (never on the presentation path), under the player's diagnostic owner; a
  stale decoder's request is ignored, a lost device is left to device-loss recovery, and Close
  cancels a pending swap. The 5 s hardware re-probe stays off for that media, so a paused first
  frame shows in software instead of black. A refused `PresentNativeFrame` does not trigger it
  (it also fails for surface reasons and already probes the device). The CD+G background video
  decoder gets no token yet (ENG-037).

Tests: `AttachesAndDetachesItsDecodeDevice_ThroughTheEngine`, `VideoDecodeFallbackTests`, and four
coordinator cases (refused frame asks once; accepted frame, no surface, or device recovery keep
hardware). The pipeline swap itself needs real hardware frames: manual.

## Manual smoke (Windows)

1. Avalonia player, Direct3D11 on WARP, hardware decoding Auto, PlayOnLoad off, an H.264 file:
   the first frame shows paused, not black. One Warning "Decoding <file> in software until it
   is opened again: ...". Play: it plays; no "Hardware video decode restored" after 5 s.
2. Same with audio: play, then pause during the switch; audio continues, the position does not
   jump. Close and reopen during the switch: no error, no old file reopened.
3. Direct3D11 on a GPU: hardware decoding stays on, no fallback Warning.
4. Batch 2 test 3 (TDR): device-loss recovery and reopen as before, not the software fallback;
   video comes back on hardware.
5. Reset and a mode switch with video open, then close: no crash, video returns; Dispose during a
   mode switch ends closed.
6. Vulkan Video and D3D11VA playback still decode on the renderer's device (status text).
