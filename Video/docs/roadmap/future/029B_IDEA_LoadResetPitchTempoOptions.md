# Idea Stage 029B - Per-Load Pitch and Tempo Reset Options

Status: Good idea. Recommended after Stage 029A.

## Why This Is Worth Doing

Karaoke and venue hosts often carry pitch/tempo offsets across media changes, so a new
track can start with stale performance settings from the previous one. Independent,
opt-in reset options let hosts start each load from neutral values without changing
default behavior for everyone.

## Recommended Direction

- Independent opt-in options `ResetPitchOnMediaLoad` and `ResetTempoOnMediaLoad` on
  session configuration; both default `false` so existing behavior is unchanged.
- Reset applies after a successful load and before autoplay begins; define
  "successful load" explicitly (load completed without failure; autoplay may still be
  pending).
- Reset targets are the neutral defaults: pitch `0`, tempo `100`.
- The two options are fully independent; enabling one never affects the other.
- Reset emits the corresponding change events (through the Stage 029A surface) with an
  explicit change reason so hosts can distinguish a load-triggered reset from a
  user-set value.

## Recommended Immediate Action

Pin the exact load-success point in the current pipeline where a reset would apply
(load completed, autoplay not yet started), and enumerate the change-reason values the
session events will carry.

## Must Preserve

- User-set pitch and tempo when the reset options are disabled (the default).
- Pitch/tempo behavior during active playback and across seeks.
- Independence from normalization; resetting pitch/tempo never touches normalization.

## Explicit Non-Goals

- Resetting parameters other than pitch and tempo.
- Heuristic "stale setting" detection; resets happen only when explicitly opted in.
- Resetting mid-playback; reset applies only at the load boundary.
