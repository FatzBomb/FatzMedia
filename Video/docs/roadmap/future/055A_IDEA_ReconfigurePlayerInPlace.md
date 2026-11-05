# Stage 055A (planned) - Reconfigure a player in place

Owner decision 2026-11-05. Queue item 6.

- `player.ReconfigureAsync(MediaPlayerConfiguration)`: the same player object (events, references and
  attached surfaces stay valid) rebuilds its engines and pipeline for the new configuration (mode,
  audio engine, renderer, device), reattaches its surfaces, reloads the current media and restores
  position, playing or paused, volume, `Normalize`, loop, tempo and pitch where the new mode allows;
  a `Reconfigured` event; media the new mode cannot play stays loaded and reports why.
- The test players' `PlayerSetController` (already shared, ENG-039 R1) uses it for renderer and mode
  changes; player-count changes still create and dispose players.
- First step: a read-only look at what holds references to the engines (the player builds its graph
  once in its constructor from a service container), reported before code.
