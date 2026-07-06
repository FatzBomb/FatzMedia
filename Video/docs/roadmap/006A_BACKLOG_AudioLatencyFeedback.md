<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: BACKLOG.

# Stage 006A — Audio Output Latency Feedback


## Status

Backlog.

## Goal

Feed measured/known audio output latency into video/CDG presentation timing.

## Why This Matters

The current audio-slaved clock model is good, but without output latency awareness it cannot fully correct device latency such as USB/Bluetooth/output-buffer delay.

## Intended Direction

- Query or estimate BASS/device output latency.
- Integrate latency into the presentation clock path without breaking tempo/pitch behavior.
- Preserve real BASS tempo for audio-backed media and clock-backed tempo for no-audio visual media.

## Not Allowed

- No full clock rewrite.
- No audio device hotplug/reinit in this stage.
- No display-rate compositor rewrite.

## First Safe Step

Map BASS position, push-buffer, output latency, and `AudioPresentationClock` interactions.

## Validation

Audio/video sync stage. Use full builds/scripts plus targeted smoke for video-with-audio, tempo changes, CDG sync, audio-only, and no-audio video/CDG regression.
