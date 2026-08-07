# Idea Stage 010A - Software Video Upload/Copy Path

Status: Good idea.

## Why This Is Worth Doing

The software video path performs full-frame CPU conversion, copying, and GPU upload.
Reducing avoidable copies improves software fallback, virtual-machine playback, low-power
systems, and future renderers that cannot consume the decoder's native resource directly.

## Recommended Direction

- Map ownership and every copy from FFmpeg output through conversion, pooling, and upload.
- Benchmark representative 1080p and 4K software-decoded inputs before and after each slice.
- Prefer mapped uploads, explicit buffer ownership, and reusable staging resources.
- Keep the current path as the correctness baseline while introducing one optimization at a time.

## Recommended Immediate Action

Create a copy-and-ownership map for one software-decoded frame and add a repeatable
benchmark that records bytes copied, allocations, upload time, and total frame time.

## Must Preserve

- Software decoder fallback correctness.
- CPU frame-pool ownership and bounded memory use.
- Retained-frame refresh behavior.
- Hardware decode and native-frame paths.

## Explicit Non-Goals

- Rewriting video decode or composition policy.
- Removing the proven software path before its replacement is validated.
- Sharing mutable decoder buffers across asynchronous consumers.
- Optimizing unrelated renderer or presentation code in the same stage.
