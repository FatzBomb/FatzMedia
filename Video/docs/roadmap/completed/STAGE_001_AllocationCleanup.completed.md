# Stage 001 — Allocation Cleanup — Completed

## Summary

Completed focused allocation cleanup without changing rendering output or public behavior.

## Completed Work

- CDG wash-color direct read removed a full snapshot allocation from `CdgEngine.RefreshBackgroundWashUnsafe`.
- Seek-frame inspection changed from materializing queued-frame arrays to direct `ConcurrentQueue` enumeration semantics.
- `VideoProcessorStream[]` reuse removed a per-frame array allocation in the D3D11 video processor path.
- Composition target/layer scratch lists and LINQ cleanup reduced per-present managed allocations.
- `MediaCompositionFrame` internal layer accessors avoided unnecessary yield/record allocation at hot access sites.
- `Marshal.SizeOf<CdgShaderConstants>()` was cached.

## Explicitly Not Completed

- CDG snapshot pooling: deferred until ownership/lifetime is redesigned.
- `VideoFrameEventArgs` reuse: deferred until event/API lifetime is redesigned.

## Completion Note

This stage is history. Do not reopen it unless a measured allocation regression points to one of these paths.
