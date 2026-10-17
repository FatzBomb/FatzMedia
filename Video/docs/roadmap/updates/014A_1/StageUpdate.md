# Stage Update

Stage: 014A.1 - Source capability matrix and network contracts
Date: 2026-10-16

## Status: IMPLEMENTED - pure contracts, no manual smoke for this slice

Delivered:

- `MediaInputSourceKind.NetworkUri`, `MediaInputSource.FromUri`, `MediaNetworkContent`.
- `MediaSourceCapabilities`, `MediaSourceClass`, `NetworkStreamProbe`,
  `MediaSourceCapabilityMatrix` (rows + `ForNetwork`), and
  `MediaSourceInfo.Capabilities` / `MediaSourceInfo.Uri`.
- `NetworkRetryPolicy` (bounded, explicit, default no retry).
- `MediaBufferingChangedEventArgs`, `MediaStreamMetadataChangedEventArgs`.
- Internal `Core/Network/IcyMetadataParser` and `BufferingStateTracker`.
- 49 focused cases (`NetworkSourceTests.cs`).
- Network sources are still rejected by `PipelineMediaEngine.Open` in this slice.

## Decisions made without the owner

- Unprobed network sources use the live row.
- Only http/https; other schemes throw at construction.
- Buffering hysteresis thresholds 5 % / 25 %, progress step 10 points.

## Verification

- Linux: Release build clean; 49/49.
