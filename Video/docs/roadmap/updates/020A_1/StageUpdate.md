# Stage Update

Stage: 020A.1 - Filter contract + bypassable equalizer
Date: 2026-10-16

## Status: IMPLEMENTED - manual Player smoke pending

Delivered: see the stage file. 14 focused cases (`AudioEqualizerTests.cs`); Player flag
`--eq=<g31,g63,g125,g250,g500,g1k,g2k,g4k,g8k,g16k>` (dB).

## Decisions made without the owner

- First filter is a parametric/graphic equalizer on BASS_FX PEAKEQ (cross-platform;
  DX8 PARAMEQ is Windows-only).
- Equalizer runs before normalization gain and fades.
- Disabled or all-0 dB settings attach nothing (bit-identical output), satisfying the
  "identical output when bypassed" rule by construction.
- Effects are recreated on `Reset`, so the equalizer returns to disabled like volume does.

## Verification

- Linux: Release build clean; 14/14 focused tests.

## Manual smoke (Windows, native DLLs)

1. `--eq=6,6,3,0,0,0,0,0,0,0 <song.mp3>`: audible bass boost; no clicks; seek, loop,
   tempo/pitch changes keep the boost.
2. `--eq=0,0,0,0,0,0,0,0,0,0`: identical to running without `--eq` (A/B by ear or by
   recording the output).
3. With Normalize on, the boosted track does not distort at full volume.
4. Next track (load another file): the boost persists.

## Review follow-up 2 (same day)

- Bands at or above 45 % of the stream's sample rate (e.g. 16 kHz on a 22.05/32 kHz file)
  are kept at 0 dB below that limit instead of being rejected by BASS_FX.
- Volume, pitch, normalization, and equalizer changes made while no audio is open now reach
  the engine on the next load (previously a pre-existing gap for volume and pitch too).
- Extra manual smoke: `--eq=...` on a 22.05 kHz MP3 raises no "band update failed" warning.
