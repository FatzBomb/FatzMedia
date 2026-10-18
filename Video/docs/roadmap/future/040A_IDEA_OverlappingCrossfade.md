# Idea Stage 040A - Overlapping Crossfade

Status: Idea. Deferred from 031B (completed 2026-10-18), which delivered fade envelopes,
single-handle fades and fade-through transitions to the prepared-next item.

## Why This Is Worth Doing

Today a transition fades the outgoing song out, then the incoming one in. A true crossfade has
both audible at once, which players and karaoke hosts expect for smooth sets.

## Recommended Direction

- A second stream owner inside `AudioEngine` (or BASSmix), so the outgoing and incoming
  handles play concurrently, with takeover when the outgoing one ends.
- Reuse 031B's envelope, schedule and runner unchanged for the two gains.
- The clock, position, end and loop logic follow the incoming item from the takeover point.
  CD+G and video for the incoming item start at takeover.

## Must Preserve

- Single-handle fades and fade-through transitions (031B).
- Prepared-next (031A), normalization, equalizer and tempo/pitch on both handles.

## Recommended Immediate Action

Prototype two concurrent BASS handles with BASSmix on Windows and measure takeover timing and
CPU before touching the engine's ownership model.
