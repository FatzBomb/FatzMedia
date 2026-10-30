# ENG-039 batch 4a: one player-set controller for the test players (R1)

2026-10-28. Branch `claude/festive-newton-yvvib5-b4a` (on top of batch 3). Status: implemented,
pending manual smoke.

## What changed

The WPF, Avalonia and WinForms test players each had their own copy of the rebuild loop (Players
or Renderer changes recreate 1-4 players), full screen with F11 and Escape, the 1 s stats line
and the closing sequence. `PlayerSetController<TSlot>` in `Player.Shared/Presentation` now owns
them; each window implements `IPlayerSetHost<TSlot>` and each player view `IPlayerSlot`
(`Player.Shared/Hosting`).
- `RebuildAsync` coalesces requests (one more build after the running one), never throws
  (failures go to the host's status line and log) and returns the build in progress.
- The first-build flag clears before anything is created, so a superseded first build still
  counts: startup media and `--second-screen` only ever open once
  (`PlayerStartupOptions.ForBuild`, `OpensSecondScreenAtStartup`).
- `ShutdownAsync` stops the stats tick, leaves full screen, waits for a running rebuild and
  disposes every player even if one fails; a second close request during shutdown is ignored.
  This replaces Avalonia's batch-1 quick fix (B6).
- One key rule: F11 toggles full screen for the focused player; Escape closes the full-screen
  player's flyout, else the focused player's, else any open one, and with none open leaves full
  screen. Avalonia handles Escape after the controls (bubbling), as WPF does, so a drop-down
  inside a flyout closes first.
- Each host keeps its own window state (WPF borderless/topmost, Avalonia `FullScreen`, WinForms
  border style and grid layout) and its own startup (Avalonia's renderer probe and warnings).

Visible differences: Avalonia's Escape outside full screen now closes an open flyout (its Log
flyout opens at startup); a player that fails to dispose also shows on the status line; WinForms
panels dispose themselves (also when their engine teardown fails) and disappear one by one on
close; WPF and WinForms show the renderer summary once per toolbar change.

Tests: `PlayerSetControllerTests` (22, fake slots and host: coalescing, dispose/create order,
first-build flag, shutdown during a rebuild, dispose failures, full-screen order, key rules,
stats tick after shutdown) and 7 `PlayerStartupOptionsTests` cases.

## Manual smoke (each of WPF, Avalonia, WinForms)

1. `--players=2 <file> --second-screen=2`: player 1 loads the file and opens its second screen;
   player 2 starts empty; each stats line updates about once a second.
2. Change Players to 4, then the Renderer: players are recreated empty, the second screen does
   not reopen, each old player logs a dispose line.
3. Change Players several times quickly: you end with the last count after one extra rebuild.
4. Change Players and press Alt+F4 at once: the window closes after every created player logged
   a dispose line; no process is left.
5. Double-click a picture: that player alone goes full screen, toolbar hidden; double-click
   again restores everything.
6. Click into player 2, press F11: player 2 goes full screen; F11 again restores.
7. Full screen with a flyout open: Escape closes the flyout, a second Escape leaves full screen.
   Windowed: Escape closes the focused player's flyout. In a flyout, Escape on an open drop-down
   closes only the drop-down. WinForms has no flyouts.
8. Close the window while full screen: it leaves full screen, disposes and closes; a second
   close click does nothing extra.
9. WPF full screen is borderless, topmost and covers the taskbar; WinForms 2x2 layout is right
   after a rebuild and after full screen; Avalonia returns to Normal or Maximized as before.
