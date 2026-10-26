# FatzBomb.MediaControl.Player.Shared

The test players' shared view model (Stage 044A). Not packable; `net10.0`; references Core and
CommunityToolkit.Mvvm only (pinned in `scripts/check-core-boundary.ps1`).

- `ViewModels/PlayerViewModel`: one player's state and commands, written against
  `MediaControlPlayer`: engine creation, transport, seek scrubbing, sync offsets, effects,
  equalizer, subtitles, visual transforms, prepared next and fades, network sources, player modes,
  flyout state and the diagnostics log. Every property changes on the UI thread; engine events are
  marshalled there. Status reads and scrub calls never block the UI thread.
- `Hosting/`: what a host supplies, bundled in `PlayerHost`: `IPlayerDispatcher` (UI thread and a
  timer), `IPlayerClipboard`, `IPlayerFilePicker`, `IPlayerEngineFactory` (renderer, platform
  package, native paths) and the debug log path. `PlayerDebugLog` and `PlayerNativePaths` are
  helpers for hosts.
- `PlayerStartupOptions`: the command line every player understands; hosts parse their own flags
  from the same arguments.

A new host implements the four interfaces, binds `PlayerViewModel`, calls `StartAsync()` once its
view is up, binds its surface to `Player`, and disposes the view model (`DisposeAsync` off the UI
thread) when it closes. Host-only features go in a derived view model (as the WPF player's
presentation mode) or in view code.
