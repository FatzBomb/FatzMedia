# Stage 060A slice 2: WinForms web media control

Status: implemented, pending the owner's manual smoke.

## What changed
- `WebMediaControl` (WinForms `UserControl`) in `FatzMedia.Video.WebView2`, the counterpart of the WPF
  `WebMediaView`: the same `InitializeAsync`, `OpenAsync(address, autoplay)` and `Player` (`IWebMediaPlayer`),
  a persistent profile in `UserDataFolder`. The project now sets `UseWindowsForms` as well as `UseWPF`.
- No new package: the WinForms `WebView2` control is in the `Microsoft.Web.WebView2` package already used.

## Decisions
- `Player.Load(url)` stays unwired; it is its own careful slice (see the 060A stage file for the design and the
  deadlock and regression risks). Hosts use `OpenAsync` on the control for YouTube links.
- Avalonia: `Avalonia.Controls.WebView` (Avalonia's own, MIT, free, needs no account or key; 12.1.2 on NuGet as of
  2026-11-13, needs Avalonia 12.0 or newer, the repo uses 12.1.4) is the planned package, added only on the owner's
  go-ahead. The free community `NativeWebView` package (MIT, one author) is the fallback.

## Manual smoke
Needs the WebView2 runtime and a network connection. In a throwaway WinForms host: add a `WebMediaControl`, set
`UserDataFolder` under `work\fatzmedia\Video`, then repeat the steps of `updates/060A_1/StageUpdate.md` (open,
play, pause, seek, volume, mute, rate, embedding-not-allowed error, sign-in kept across a restart).
Scratch check before the PR (a WinForms harness against YouTube): loaded (duration 1344 s), played, seeked to 60 s,
paused.
