# Completed Stage 019A - Video Frame Event Ownership and Reuse

Prepared: 2026-10-16
Status: Completed 2026-10-18. Slice 1 (consumer and lifetime matrix, written ephemeral
contract, conformance tests); no behaviour change, so no owner smoke beyond the regression runs
(`updates/019A_1`). An explicit frame lease belongs with the 010A follow-up in
`future/041A_IDEA_EngineInternalFollowUps.md`.

## Producer Matrix (pinned)

| Producer | Event | PixelData backing | `PixelDataCanBeRetained` | NativeResource | Instance reuse |
|---|---|---|---|---|---|
| `VideoPipeline.RaiseFrameReady` (primary video) | `IVideoEngine.FrameReady` | decoder `ArrayPool` frame; returned when the *next* frame replaces it | false | hardware frames: decoder-owned texture | new args per raise (incl. reissue) |
| `VideoPipeline.TryPresentLoopStartSnapshot` | `FrameReady` | private copy taken once per open | true | none | cached instance re-raised |
| `PipelineMediaEngine.RaiseBackgroundFrame(decoder, frame)` (sidecar video) | `IMediaEngine.BackgroundFrameReady` | decoder pool array, returned in `finally` right after the event | false | hardware frames: decoder-owned texture | new args per raise |
| `PipelineMediaEngine.RaiseBackgroundFrame(source)` (image background) | `BackgroundFrameReady` | host `MediaInputSource` array | true | none | new args |
| `PipelineMediaEngine.RaiseImageFrame(source)` / `(decoder, frame)` | `ImageFrameReady`, `TryGetCurrentImageFrame` | host array / private copy | true | none | cached as current image, re-read for refresh |

## Consumer Matrix (pinned)

| Consumer | Kind | What it keeps |
|---|---|---|
| `MediaPresentationCoordinator.SetCompositionLayer0Frame` | synchronous | copies pixels into its `RetainedFrameBuffer` (010A); never stores the args |
| Coordinator hardware paths (`PresentNativeFrame`, `RenderHardwareVideoCompositionFrame`, `StoreRetainedHardwareBackgroundFrame`) | synchronous | only dimensions/revision; the resource is used inside the renderer call |
| `IMediaRendererBackend.PresentNativeFrame` / `EnsureNativeFrameDevice` (Direct3D11) | synchronous | reads handles during the call; Direct3D11 may adopt the decoder *device* (not the frame) |
| Software / Vulkan backends | n/a | native frames rejected |
| Hosts | none | frame events are not exposed through `MediaControlPlayer` or the UI adapters |

No consumer is asynchronous or retaining; no host observes the events.

## Contract (now documented on `VideoFrameEventArgs`)

Ephemeral by default: the args object and payloads are valid for the callback only;
consumers copy what they keep and never store the args; `PixelData` is retainable only
when flagged; `NativeResource` is always borrowed; producers may re-raise a cached
retainable instance but never mutate a raised one.

## API Impact Evaluation

- An explicit lease type (`VideoFrameLease : IDisposable`) would let the coordinator skip
  copy 3 of 010A, but every raise would need a release path through the pipeline and
  renderers; it belongs with 010A copy-3 work, not here.
- Reusing one args instance per producer saves ~80 bytes per frame (<5 KB/s at 60 fps),
  which the stage non-goals rule out as a reason to change public semantics. Not done.
- Decision: keep the type and flags; enforce the contract by tests.

## Acceptance Criteria (slice 1)

- Producer and consumer matrices recorded (above).
- Contract written into the public XML docs.
- Tests: ephemeral payload reuse after the callback cannot change the retained frame; a
  retainable payload is still copied; neither the args object nor an ephemeral payload is
  reachable from the consumer after the callback; an invalid frame clears the retained one.

## Why This Is Worth Doing

Clarifying video-frame event ownership would reduce ambiguity for hosts and renderers,
make retention rules enforceable, and create safe opportunities to reduce per-frame
event and wrapper allocations.

## Recommended Direction

- Classify every event consumer as synchronous, asynchronous, retaining, or observing.
- Separate ephemeral callback data from resources that require an explicit lease.
- Design lifetime rules as public API behavior before adding reuse or pooling.
- Provide migration tests for image, background-video, and host event consumers.

## Recommended Immediate Action

Create a consumer/lifetime matrix for `VideoFrameEventArgs` and its native or managed
payloads. Draft one explicit ephemeral-or-leased contract and evaluate its API impact.

## Must Preserve

- Existing frame validity during callbacks.
- Image-frame and retained-background behavior.
- Native frame ownership and release safety.
- Host event exception isolation.

## Explicit Non-Goals

- Silently reusing the existing public event object with different lifetime semantics.
- Exposing mutable shared frame state.
- Pooling native resources without producer coordination.
- Treating fewer allocations as more important than API correctness.
