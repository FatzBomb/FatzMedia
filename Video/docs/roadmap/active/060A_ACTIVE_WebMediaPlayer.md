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
- **Slice 2 (not started):** Avalonia `NativeWebView` adapter, WinForms WebView2 control, wiring web items into
  `Player` so `Player.Load(url)` plays them, Premium no-ads re-check in the real app.
