<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: FUTURE.

# Future Stage 022A — Thread Model Profiling / Reduction Gate


## Status

Future. Proposed order: 022A.

## Why This Exists

Thread-count reduction is not a goal by itself. The current thread model is not automatically wrong; the real problems are contention, blocking, and work under the wrong locks.

## Not Active Until

Promote only if profiling shows excessive context switching, thread wakeups, lock contention, idle CPU, or scheduler complexity causing bugs.

## Intended Direction

Use profiling evidence to simplify thread ownership only where it improves behavior.

## Must Not Break

- Decode/presentation separation.
- Seek pump behavior.
- Operation ordering.
- CDG coordination.
- Audio/video sync.

## First Safe Step

Capture thread/CPU/lock contention evidence during real playback and seek scenarios, then decide whether reduction is warranted.
