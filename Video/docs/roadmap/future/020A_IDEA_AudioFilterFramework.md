# Idea Stage 020A - Audio Filter Framework

Status: Good idea.

## Why This Is Worth Doing

An ordered audio-processing chain enables EQ, compression/limiting, ducking, reverb,
vocal tools, and future per-output processing without continuing to add one-off controls
around the BASS/BassFx path.

## Recommended Direction

- Define explicit filter ordering, bypass, parameter, latency, and lifecycle contracts.
- Keep native processing off managed per-sample callback paths where practical.
- Report filter latency to the presentation clock when a filter adds measurable delay.
- Deliver one bypassable filter end to end before generalizing the framework.

## Recommended Immediate Action

Map the current volume, normalization, tempo, and pitch signal chain. Select one small
first vertical slice, preferably a bypassable equalizer, and specify its ordering,
latency, persistence, and runtime-update behavior.

## Must Preserve

- Existing pitch, tempo, volume, and normalization behavior.
- Audio/video synchronization and output-latency correction.
- Low-latency venue operation.
- Identical output when all added filters are bypassed.

## Explicit Non-Goals

- Building a DAW, plugin host, or arbitrary third-party DSP loader.
- Running unrestricted managed code on the real-time audio callback.
- Changing default sound merely by enabling the framework.
- Implementing every proposed filter in one stage.
