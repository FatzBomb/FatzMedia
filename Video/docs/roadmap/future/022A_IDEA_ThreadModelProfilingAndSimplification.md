# Idea Stage 022A - Thread Model Profiling and Simplification

Status: Good idea.

## Why This Is Worth Doing

A documented and measured thread model improves maintainability even when no immediate
failure exists. It can expose unnecessary wakeups, lock contention, blocking, and
unclear ownership while creating a baseline for future scheduler changes.

## Recommended Direction

- Document every long-lived thread, scheduler, callback thread, lock, and cancellation path.
- Capture CPU, wakeup, context-switch, and lock-contention traces during real workflows.
- Simplify one ownership boundary at a time when the new model is easier to prove correct.
- Keep thread count as an observation, not the success metric.

## Recommended Immediate Action

Capture an ETW or `dotnet-trace` baseline for open, play, seek, pause, loop, stop, and
close across audio, CDG, and video. Pair the trace with a thread-ownership diagram.

## Must Preserve

- Decode/presentation separation.
- Latest-wins seek behavior and operation ordering.
- CDG/subtitle coordination.
- Audio/video synchronization.
- Cancellation and disposal safety.

## Explicit Non-Goals

- Reducing thread count for its own sake.
- Merging independent loops into one oversized scheduler.
- Replacing measured native waits with busy loops.
- Combining thread changes with unrelated media behavior changes.
