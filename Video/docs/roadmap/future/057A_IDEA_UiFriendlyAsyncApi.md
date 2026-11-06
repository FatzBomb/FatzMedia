# Stage 057A (planned) - UI-friendly async API

Owner decision 2026-11-05, from the test-player audit (what the players do by hand that every app
would have to repeat). Queue: after 055A unless moved.

1. **`BeginScrubAsync` / `EndScrubAsync`.** `BeginScrub` and `EndScrub` are synchronous and wait for the
   control queue (`MediaControlPlayer.cs`), against the rule that UI code uses async control APIs. The
   shared view model builds its own ordered task chain with a newest-wins target for them
   (`Player.Shared/ViewModels/PlayerViewModel.Seek.cs`, the scrub lane, about 80 lines). Core keeps begin,
   the drag seeks and end in order on the control queue; the lane goes.
2. **`DisposeAsync` on the player.** All three windows push `Dispose` to a background thread and hold
   their own close for it (`PlayerViewModel.cs` `Task.Run(player.Dispose)`, `MainWindow.xaml.cs`,
   `MainWindow.axaml.cs`, `MainForm.cs`), and each panel sets `Surface.Player = null` first. Core
   implements `IAsyncDisposable`; optionally the adapters' surfaces let go of a player that is disposed.
3. **The players use the events Core already raises.** The view model polls `State`, `Duration` and
   `Position` every 200 ms on `Task.Run` (`PlayerViewModel.Status.cs`, ENG-035) with a 1 s hold after a
   seek, while Core raises `StateChanged`, `PositionChanged` (every 250 ms while playing) and
   `SeekCompleted` (with a fresh snapshot). Switch the shared view model to them (the read-stall warnings
   can stay as test diagnostics) and say in the docs that apps should use the events, not poll.

Tests: Core ordering and dispose through the existing doubles; the players' scrub feel and window close
by hand on Windows.
