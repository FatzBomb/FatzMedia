<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: BACKLOG.

# Stage 006B — Audio Device Hotplug


## Status

Backlog.

## Goal

Handle audio device/default-device changes without silent audio failure.

## Why This Matters

Venue systems often use USB interfaces, HDMI audio, Bluetooth, or changed default devices. A production engine should surface or recover from output changes.

## Intended Direction

- Detect output/default-device changes if practical.
- Surface useful error/diagnostic information.
- Reinitialize or fail gracefully based on the safest supported behavior.

## Not Allowed

- Do not combine with latency feedback unless explicitly promoted together.
- Do not rewrite the whole audio engine.
- Do not break existing BASS tempo/pitch/normalization behavior.

## First Safe Step

Map current BASS device initialization and failure behavior.

## Validation

Audio behavior stage. Use targeted smoke for audio-only, audio+CDG, video-with-audio, pitch/tempo/normalization, and intentional device failure/change if reproducible.
