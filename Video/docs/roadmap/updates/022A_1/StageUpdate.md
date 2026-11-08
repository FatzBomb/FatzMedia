# Stage Update

Stage: 022A.1 - Thread ownership map and capture tooling
Date: 2026-10-16

## Status: IMPLEMENTED (documentation + tooling) - Windows baseline captured 2026-10-17 (stage completed 2026-10-18)

Delivered: the thread-ownership map, synchronization/cancellation summary, baseline
workflow table, and `scripts/capture-thread-baseline.ps1` (validated on Linux against a
live .NET process). No production code changed.

## Decisions made without the owner

- Did not change any thread, loop, or scheduler: the stage requires a measured baseline
  first, and real playback (BASS, D3D11, hardware decode) cannot run in the Linux sandbox.
- `dotnet-trace` 10 renamed profiles; the script uses `dotnet-sampled-thread-time` plus
  runtime Contention/Threading keywords rather than the Linux-only `cpu-sampling` profile.
- Captures go to `artifacts/thread-baseline/` (already git-ignored).

## Manual steps (Windows, native DLLs)

For each label in the stage file's workflow table:

1. Start the Player (`dotnet run --project FatzBomb.MediaControl.Player -c Release`).
2. `pwsh scripts/capture-thread-baseline.ps1 -Label <label> -DurationSeconds 60`
3. Perform the workflow during the capture window.
4. Keep the CSV + nettrace; note anything that stutters. Attach them to the next 022A
   slice, which picks one simplification from the data.
