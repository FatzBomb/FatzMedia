# Completed Stage 020A - Audio Filter Framework

Prepared: 2026-10-16
Status: Completed 2026-10-18. slice 1 (audio filter contract and bypassable parametric
equalizer); owner smoke passed 2026-10-17 (`updates/020A_1`). Video-file audio uses the same
chain since 033A.

## Signal Chain Map (pinned)

Playback stream: decode -> BASS_FX tempo/pitch (tempo stream) -> **filter stages** ->
normalization gain FX -> fade envelope FX (031B) -> channel volume attribute (user
volume) -> output device. BASS runs FX with higher priority first: equalizer priority 2,
gain and fade priority 0. Every stage is 32-bit float, so boosts do not clip inside the
chain. Output-latency correction (`IOutputLatencyProvider`) is unchanged.

## Framework Contract

- `IAudioFilter`: stable id, BASS priority (stage order), reported latency, `IsActive`
  (bypass = no native stage at all), `Apply(playHandle)`, `Remove()`.
- Lifecycle: filters are forgotten when their stream is freed (open/close/URL open) and
  re-applied to every new playback stream after the gain and fade stages are created.
- Runtime updates: same band count -> parameters updated in place (no gap); count change
  -> rebuild; not effective -> remove.
- Latency: filters report seconds of added delay; the IIR equalizer reports 0. A filter
  with measurable delay must feed it into the presentation clock (future filters).
- No managed per-sample callbacks; native BASS/BASS_FX DSP only.

## Slice 1 - Equalizer

- Public `AudioEqualizerBand` (20 Hz..20 kHz, +/-15 dB, 0.1..4 octaves) and
  `AudioEqualizerSettings` (disabled default, <= 16 bands, value equality,
  `CreateGraphic10Band`).
- BASS_FX `PEAKEQ` (`BassEqualizerFilter`), applied through internal `IAudioFilterHost`
  on the BASS `AudioEngine` (plain audio, audio+CDG, archives, network audio).
- `MediaEffects.Equalizer` / `IMediaControlSession.Equalizer` + `EqualizerChanged`.
- Persistence is host-owned (settings are an immutable value the host serializes).

## Explicit Non-Goals

As in the original idea: no DAW/plugin host, no managed real-time callbacks, no default
sound change, not every filter at once. Video-file audio (decoded by the video pipeline)
is not equalized in this slice; Stage 033A slice 1 adds it.
