# Stage Update

Item: audit 2026-11-12, T1, T2, T3, U2, U5 - test player and adapter drift
Date: 2026-11-12

## Status: implemented, pending manual smoke

What PR 19-21 already did: the CD+G look controls and `PlayerViewModel.CdgLook` (WPF only). This item brings the
other players level and removes drift.

Delivered:
- **Avalonia test player (the reference player):** the CD+G look in the Video flyout (scale, style, outline, shadow
  size and distance, bevel strength and size, Reset look); a busy bar while `IsBusy` or `IsSwitchingMode` with
  `LoadingMediaText`; the startup-error banner (`StartupError`); the transport state (`PlaybackState`); the A/B swap
  demo (`--ab-demo=`, `--ab-swaps=`, `--ab-interval=`, `--ab-collapsed`, `--ab-exit`; the waiting surface is hidden
  with `Opacity = 0`, `--ab-collapsed` uses `IsVisible = false`; `--ab-mode=native` has no meaning on Avalonia).
- **WinForms test player:** the same CD+G look controls on the Video tab.
- **Avalonia `MediaSurface`:** `OverlayContent` and `OverlayHitTestVisible`. The overlay fills the surface and is a
  visual child after a new internal frame host that carries the shared GPU image's composition visual, so it is
  drawn above the video on both presentation paths. The surface still asks for no size of its own.
- **WPF `MediaSurface`:** the `[Obsolete]` `ShowSecondaryOutput`, `CloseSecondaryOutput`, `IsSecondaryOutputOpen`
  and `SecondaryOutputsChanged` are deleted with the window field they managed. Hosts open a
  `SecondaryPresentationWindow` (the test players already do).
- **View model:** `Options` and `CanUseSync` are private. `PlaybackState`, `IsSwitchingMode` and `StartupError` are
  now bound by the Avalonia player.
- **`scripts/check-presentation-parity.ps1`** now also checks every adapter's `OverlayContent` and
  `OverlayHitTestVisible`, that no player drops a CD+G look member, the Avalonia busy/startup-error/A-B pieces, and
  that the removed WPF secondary-output members do not return.

Manual smoke (not run here, UI):
1. Avalonia player: open a `.cdg`; in Video, change scale/style/outline/shadow/bevel and Reset look: the picture
   follows live. Same on the WinForms player's Video tab.
2. Avalonia player with a missing native library: the red banner shows the reason; open a file and watch the busy bar.
3. `FatzMedia.Video.Sample.Player.Avalonia --ab-demo=<folder of clips> --ab-swaps=6`: the window swaps clips; the
   panel colour changes with each swap; the trace has `[ABDemo] swap i/N ready after ...`. Try `--surface=framesink`.
4. An Avalonia app that sets `MediaSurface.OverlayContent` (Kamujo / TriviaBomb style): the overlay is visible above
   the video on the shared GPU path and on the frame-sink path.
