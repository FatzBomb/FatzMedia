# Stage 060A (active) - Web media player (YouTube first)

Owner 2026-11-13. Built into FatzMedia.Video, not a separate project. Design: `../../WEB_PLAYER_DESIGN.md`.

- `Player.Open(url)` detects a supported web service and plays it through the system web view. Release
  scope: **YouTube only**, on Windows (WPF, WinForms) and Avalonia first; other platforms later.
- Later, one at a time: Vimeo, Dailymotion, Spotify, Deezer (check that new developer apps are open),
  KaraFun (blocked: needs an OEM partner agreement; no `.kfn` decoder).
- **Core** gets only a small provider contract and URL detection: no WebView reference, no `[DllImport]`;
  the boundary scripts stay green (`check-core-boundary.ps1` updated on purpose if a project is added).
  **Adapters** supply the web view (WebView2, Avalonia `NativeWebView`; later WKWebView and Android
  WebView) and only pass messages.
- A web item takes over the media surface. FatzAudio routing, key and tempo, mixing, crossfade,
  extraction and the CD+G overlay report `NotSupported` for it.
- Sign-in: `SignIn`/`SignOut` open the service's login in the same persistent web view profile.
  FatzMedia never sees passwords or tokens.
- No extraction, caching or background playback (YouTube developer policies).
- **First task: a spike**, before any design is final. It must show: (1) Google sign-in works in the
  embedded view; (2) YouTube Premium no-ads applies to the embed; (3) sign-in cookies work in the embed;
  (4) the Referer / error 153 fix (serve the player page from a virtual https host); (5) Avalonia
  `NativeWebView` coverage on macOS and Linux. Results decide the scope.
- Effort: about 1 to 2 weeks after the spike.

## Slices
- **Slice 1 (implemented, pending manual smoke):** Core contract (`IWebMediaProvider`, `WebMediaProviders`,
  `IWebMediaPlayer`, `WebMediaSupport`), YouTube address detection, `MediaInputSourceKind.WebMedia` and
  `MediaInputSource.Parse`; the WebView2 adapter `FatzMedia.Video.WebView2` (`WebView2MediaPlayer`, the WPF
  `WebMediaView`). Spike results: `docs/roadmap/updates/060A_1/SpikeResults.md`. `updates/060A_1/StageUpdate.md`.
- **Slice 2 (WinForms implemented, pending manual smoke):** the WinForms `WebMediaControl` in
  `FatzMedia.Video.WebView2` (`updates/060A_2`). The Avalonia adapter waits for the owner's go-ahead on the
  package (`Avalonia.Controls.WebView`, MIT, free; see `updates/060A_2/StageUpdate.md`).
- **Slice 3 (Avalonia implemented on Windows, pending manual smoke):** `FatzMedia.Video.WebView2.Avalonia`
  (`AvaloniaWebMediaPlayer`, `WebMediaView`, a loopback page server) on Avalonia's `NativeWebView`
  (`Avalonia.Controls.WebView` 12.1.2, MIT, $0, owner OK 2026-11-13). `updates/060A_3`.
- **Slice 4 (implemented, pending manual smoke): `Player.Load(url)` plays web items.** Owner 2026-11-13 ("wire
  Player.Load now"; earlier "later" overridden). `PlayerCore.AttachWebMediaPlayer(IWebMediaPlayer)`; a YouTube
  address passed to `Load`/`LoadAsync` plays in the attached web view; Play, Pause, Stop, Seek, Position,
  Duration, State, IsOpen, volume and the events follow; every other source takes the engine path unchanged.
  Design, deadlock rule and tests: `updates/060A_4/StageUpdate.md`.
- **Follow-up (code review):** `WebMediaView` (WPF) and `WebMediaControl` (WinForms) duplicate their
  `UserDataFolder`, `InitializeAsync`, `OpenAsync` and `Player`; move that into one non-UI helper when a third
  host (Avalonia) is added.
