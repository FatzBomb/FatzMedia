<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: BACKLOG.

# Stage 005C — Decoder Device-Lost Recovery


## Status

Backlog. Deferred out of Stage 005B scope; do not start until Stage 005B (presentation-device
recovery) is implemented and validated.

## Goal

Detect and recover the FFmpeg hardware-decode D3D11 device when it is lost/removed/reset,
separately from the presentation-device recovery already implemented in Stage 005B.

## Why This Matters

Stage 005B recovers the presentation device, swap chains, DirectComposition target, and secondary
outputs. It does not address the FFmpeg-owned hardware-decode device — a distinct `ID3D11Device`
attached through `D3D11DeviceResources.InitializeFromNativeDevice`, wired from the FFmpeg hardware
decoder rather than created by Core. If that device is lost, hardware-accelerated video decode can
fail even after presentation has fully recovered, silently degrading or stopping video playback.

## Intended Direction

- Detect device-lost conditions on the FFmpeg-owned hardware-decode device, separately from the
  presentation-device detection already added in Stage 005A.
- Recover/rebuild the hardware-decode device path without touching decoder architecture, the
  shared-demuxer input layer, or the presentation-device recovery path from Stage 005B.
- Preserve Core ownership of all decode/device state; no WPF adapter involvement.
- Coordinate with (but do not merge into) the Stage 005B presentation-recovery orchestrator —
  the two devices are independently owned and may fail independently.

## Not Allowed

- No decoder or shared-demuxer architecture rewrite.
- No renderer abstraction rewrite.
- No cross-platform backend work.
- No changes to the Stage 005B presentation-device recovery orchestrator beyond what is strictly
  needed to coordinate (e.g., avoid both recovery paths racing on the same underlying adapter).

## First Safe Step

Map how the FFmpeg hardware-decode device is created, attached, and owned (`VideoDecoder`,
`D3D11DeviceResources.InitializeFromNativeDevice`, and the FFmpeg hardware-decode call sites that
would first observe a lost device), and identify where a decode-side device-lost condition would
first surface — before designing detection or recovery.

## Validation

High-risk native/decoder stage. Use full build/boundary/parity checks plus targeted
hardware-accelerated video playback smoke tests across a simulated device-lost condition
(GPU adapter disable/re-enable or driver TDR), confirming decode recovers independently of and
without disrupting presentation-device recovery.
