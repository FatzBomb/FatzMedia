# Active Stage 010A - Software Video Upload/Copy Path

Prepared: 2026-10-16
Status: Slice 1 (copy map, repeatable benchmark, removal of the per-composition snapshot
copy) implemented; owner smoke passed 2026-10-17.

## Copy and Ownership Map - one software-decoded frame (pinned)

| # | Step | Owner of the buffer | Bytes | Status |
|---|---|---|---|---|
| 0 | FFmpeg decode into `AVFrame` (YUV) | FFmpeg | planes | native, required |
| 1 | `sws_scale` YUV -> BGRA directly into an `ArrayPool<byte>.Shared` array | `VideoDecoder` frame queue -> `VideoFrameData` | W*H*4 | required conversion, no extra copy |
| 2 | `VideoFrameEventArgs` to the presentation coordinator | borrowed (`PixelDataCanBeRetained=false`) | - | no copy |
| 3 | `SetCompositionLayer0Frame` retains the frame | `RetainedFrameBuffer` (pooled, ref-counted) | W*H*4 | **copy kept** (decoder buffer is returned to its pool) |
| 4 | Composition snapshot for each present | previously a rented copy; now a reference to the retained buffer | W*H*4 | **removed in slice 1** |
| 5 | Renderer upload (D3D11 texture / Vulkan staging / software compose) | renderer | W*H*4 | renderer-owned |
| - | Loop-start snapshot (`CacheLoopStartFrameSnapshot`) | `VideoPipeline` | W*H*4 once per open | unchanged |

## Benchmark (`SoftwareVideoCopyBenchmark`, opt-in)

`FATZBOMB_LINUX_FFMPEG_DIR=<FFmpeg 9.0.x libs> FATZBOMB_BENCHMARK_VIDEOS="a.mp4;b.mp4" dotnet test FatzBomb.MediaControl.Linux.Tests --filter SoftwareVideoCopyBenchmark --logger "console;verbosity=detailed"`

Linux sandbox, 4 vCPU, FFmpeg 8.1, MPEG-4 Part 2 test clips (2026-10-16):

| Frame | Bytes/frame | Decode+convert | Copy 3 | Removed copy 4 |
|---|---|---|---|---|
| 320x240 | 307,200 | 0.23 ms | 0.013 ms | 0.014 ms |
| 1920x1080 | 8,294,400 | 1.05 ms | 0.68 ms (12 GB/s) | 0.68 ms |
| 3840x2160 | 33,177,600 | 5.35 ms | 3.07 ms (11 GB/s) | 2.98 ms |

Managed allocations per frame are pool warm-up only (queue depth x frame size), not
steady state.

## Slice 1 Change

`RetainedFrameBuffer` (pooled, reference-counted). The coordinator holds one reference
while a buffer is current; each composition built under `_lock` takes another and releases
it after `PresentComposition`. `SetCompositionLayer0Frame` overwrites in place only when no
composition holds the buffer (checked under `_lock`), otherwise it retains into a fresh
pooled buffer; the last release returns arrays to the existing 64 MB-bucket composition
pool. Renderers already had to finish with `BackgroundBgra` inside `PresentComposition`
(the old rented copy was returned right after), so the contract is unchanged.

## Next Slices

1. Remove copy 3 when the decoder can hand over pool ownership (frame lease from
   `VideoDecoder` to the coordinator instead of copy + return).
2. Direct3D11: map-discard dynamic texture upload from the retained buffer (measure).
3. ~~Software renderer: skip the background blend for opaque full-coverage frames.~~ Done
   by ENG-017 (2026-10-18): opaque source pixels are copied, not blended.

## Explicit Non-Goals

As in the original idea.
