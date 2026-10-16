# Active Stage 031A - Host Playlist and Core Prepared-Next Slot

Prepared: 2026-10-16
Status: Implemented, pending manual smoke (see `updates/031A_1/StageUpdate.md`).

## Objective

Give hosts a Core-owned depth-one prepared-next slot so the next item is analyzed ahead
of time and the transition into it is immediate, without Core owning a playlist or queue.

## Scope (exact)

- Public additive API on `IMediaControlSession` / `MediaControlPlayer`:
  `PreparedNext` (snapshot), `AutoTransitionToPreparedNext`, `AnalyzeAsync`,
  `PrepareNextAsync`, `ClearPreparedNext`, `TransitionToPreparedNextAsync`, and the
  `PreparedNextChanged` / `PreparedNextTransitioned` events.
- Internal `PreparedNextSlot` state machine: Empty -> Analyzing -> Ready ->
  Transitioning -> Empty, with Stale entered from Ready when the prepared file changed.
- Internal `IMediaFileAnalyzer` implemented by the BASS `AudioEngine` on a dedicated
  decode stream; it warms the Stage 030B media-analysis and normalization-gain caches.
- Optional automatic transition at natural media end.

## Pinned Decisions

1. **Depth one.** `PrepareNextAsync` replaces any existing candidate; the replaced
   candidate's analysis is cancelled and its call returns `false`.
2. **Generations.** Every accepted candidate gets a new generation. Completion,
   failure, and transition completion from an older generation are ignored, so a
   cleared or replaced item can never resurrect the slot.
3. **Cancellation.** `ClearPreparedNext`, replacement, the caller's token, `Reset`, and
   `Dispose` cancel analysis. The slot owns no media resources, so clearing never
   touches current playback. Standalone analyses own their own decode handle and are
   joined (bounded 2 s) during `AudioEngine.Dispose`.
4. **Stale.** "Stale on media change" means the prepared *file* changed on disk
   (path/length/last-write identity, matching the 030B cache key) between preparation
   and transition. A Stale item is not loaded; the host clears or re-prepares it.
   Loading other media as current does not stale the slot, because the host owns
   ordering.
5. **Analysis coverage.** Only local plain non-tracker audio files are analyzed.
   Video, CDG, images, tracker formats, and in-memory BGRA sources reach Ready with
   `Analysis = null`. An analyzer failure is a warning diagnostic and still reaches
   Ready without analysis; the transition load reports real failures.
6. **Transition.** Runs on the control queue: claim Ready -> `Transitioning`, load the
   source, then start playback (`PlayOnLoad` handles it when enabled, otherwise an
   explicit `Play`). The slot empties after the attempt whether or not it succeeded, so
   a broken item is never retried silently.
7. **Automatic transition** (default off) is queued behind both the host `MediaEnded`
   delivery and Core's end completion. It is skipped if `LoopMedia` is on, auto was
   disabled, the host changed transport (Play/Pause/Stop/Seek), loaded other media, or
   cleared/replaced the slot in the meantime.
8. **Events.** Slot events are raised inline when already on the control queue (so
   they stay ordered with the load events of the same operation) and are queued
   otherwise, matching the Stage 029A surface.
9. **Reset** clears the slot because its analysis ran on the old engine.
10. **Fade/crossfade** hooks from Stage 031B are not wired in this stage.

## Acceptance Criteria

- Depth-one bound, replacement cancellation, clear cancellation, caller-token
  cancellation, generation guards, Stale detection - covered by `PreparedNextSlotTests`.
- Prepare does not affect current playback; analysis failure degrades to Ready without
  analysis; missing file is rejected - covered by `PreparedNextPlayerTests`.
- Transition-now loads and plays, empty slot returns false, load failure empties the
  slot, Stale is not loaded.
- Automatic transition fires after `MediaEnded`, is skipped when disabled, when the host
  stops in `MediaEnded`, and while looping.
- `Reset` clears the slot. Event order Analyzing -> Ready -> Transitioning -> Empty.
- Native: standalone analysis warms the cache so the transitioned item opens with the
  cached trim window and gain (manual smoke).

## Explicit Non-Goals

- Playlist or queue management, persistence, ordering, or library ownership in Core.
- Multi-item look-ahead.
- Pre-opening decoders or audio streams for the next item (preparation is analysis
  only; the transition performs a normal load).
