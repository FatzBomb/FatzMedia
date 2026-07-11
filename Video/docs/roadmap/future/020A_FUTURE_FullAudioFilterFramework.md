<!-- Conditional future stage. Promote only when its activation condition is met. -->

Stage file status: FUTURE.

# Future Stage 020A — Full Audio Filter Framework


## Status

Future. Proposed order: 020A.

## Why This Exists

A future host product may need ordered audio processing beyond current volume/pitch/tempo/normalization: EQ, compressor/limiter, ducking, reverb, vocal tools, or per-output effects.

## Not Active Until

Do not promote until core audio device/latency behavior is stable and specific product features are chosen.

## Intended Direction

Move toward a configurable audio processing chain without breaking BASS/BassFx tempo/pitch behavior.

## Must Not Break

- Existing pitch/tempo.
- Normalization.
- Audio/video sync.
- Low-latency venue behavior.

## First Safe Step

Define required filters and ordering; map current BASS/BassFx/normalization chain before source edits.
