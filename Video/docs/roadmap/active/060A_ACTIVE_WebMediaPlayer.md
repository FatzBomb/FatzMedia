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
- **Slice 3 (deferred by the owner 2026-11-13: "later, own careful slice"): `Player.Load(url)` plays web items.**
  Design review (recommendation, 70% confidence, not yet read against `MediaFile` and `MediaControls`):
  a Core `WebMediaSession` owned by `PlayerCore`; a host attaches its `IWebMediaPlayer` (through `PlayerOptions` or
  `AttachWebMediaPlayer`); `MediaFile.Load/LoadAsync` close the pipeline engine and hand a `WebMedia` source to the
  session; `MediaControls.Play/Pause/Stop/Seek` and `PlayerCore` `IsOpen/State/Position/Duration/volume` read from the
  session while it is active; web state and error events map onto the existing events under
  `DiagnosticRouter.EnterOwner`; pitch, tempo, EQ, CD+G, fades and prepared-next report not supported through one
  guard (`WebMediaSupport.Describe`). The web-active flag changes only inside a control-queue turn. Do not wrap
  `IWebMediaPlayer` as an `IMediaEngine`. Risks: the synchronous `Load`/`Pause` block the UI thread while WebView2
  needs it (the adapter must marshal internally and sync web calls must not wait on the web view); web events from a
  replaced item (tag with the load generation); regressions to normal playback (pin with `TestMediaEngine` tests
  that run with no session attached).
- **Follow-up (code review):** `WebMediaView` (WPF) and `WebMediaControl` (WinForms) duplicate their
  `UserDataFolder`, `InitializeAsync`, `OpenAsync` and `Player`; move that into one non-UI helper when a third
  host (Avalonia) is added.
