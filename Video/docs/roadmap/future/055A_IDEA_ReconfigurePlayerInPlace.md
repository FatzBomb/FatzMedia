# Stage 055A (planned) - Reconfigure a player in place

Owner decision 2026-11-05. Queue item 6.

**Already there (043A slice 2):** `SetModeAsync(MediaPlayerMode)` switches a player's mode on the fly,
reopening the open media at its position and in its transport state, and raises `ModeChanged`.

**This stage extends that to the rest of the configuration:** `player.ReconfigureAsync(MediaPlayerConfiguration)`
on the same player object (events, references and attached surfaces stay valid) for the settings
`SetModeAsync` does not cover: audio engine (BASS or FatzAudio), renderer, audio device, normalization
options, logging. It reuses `SetModeAsync`'s reopen-at-position path, restores volume, `Normalize`,
loop, tempo and pitch where the new configuration allows, and raises `Reconfigured`; media the new
configuration cannot play stays loaded and reports why.

- The test players' `PlayerSetController` (already shared, ENG-039 batch 4a) uses it for renderer and
  audio-engine changes; player-count changes still create and dispose players.
- First step: a read-only look at how `SetModeAsync` rebuilds the service graph and what else holds
  engine references, reported before code.
