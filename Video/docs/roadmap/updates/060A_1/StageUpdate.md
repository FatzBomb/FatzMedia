# Stage 060A slice 1: web media in a WebView2 (YouTube)

Status: implemented, pending the owner's manual smoke.

## What changed
- **Core (`FatzMedia.Video`, no web view):** `WebMediaProviders.Parse` recognises YouTube addresses by host
  (`youtube.com`, `www.`, `m.`, `music.youtube.com`, `youtu.be`; `watch?v=`, `youtu.be/`, `embed/`, `shorts/`,
  `live/`). Playlist, channel, search and bare-home addresses are `Unsupported` with a reason; every other
  address is `NotWebMedia` and keeps going to network playback unchanged. `MediaInputSource.Parse(string)` makes
  a web source, a network source or a file source from text. `IWebMediaPlayer` is the contract an adapter
  implements; `WebMediaSupport` says what a web item cannot do (FatzAudio routing, key, tempo, mixing, CD+G
  overlay, extraction, background playback). New `MediaErrorCode.WebMediaUnavailable`: the media engine rejects a
  web source, because it belongs to a web view. Background and Audio player modes reject web sources.
- **`FatzMedia.Video.WebView2` (new, `net10.0-windows7.0`, references Core only):** `WebView2MediaPlayer`
  (works on any `CoreWebView2`), the WPF `WebMediaView` control. The player page is embedded in the assembly and
  served from the virtual https host `web.fatzmedia.test` (answers requests with `WebResourceRequested`), which
  avoids YouTube error 153. IFrame API control (play, pause, seek, volume, mute, rate), state and error events,
  position every 250 ms, persistent profile folder, `SignInAsync` (Google's own sign-in page in the same view)
  and `SignOutAsync` (clears the profile). FatzMedia never sees a password or token.
- Boundary scripts updated for the new project; tests in `FatzMedia.Video.Tests` (address detection, source
  kind, unsupported features, YouTube error codes, page message parsing).

## Decisions
- `Player.Load(url)` is not wired to web items yet: a host calls `MediaInputSource.Parse` and, for a web
  source, opens it in `WebMediaView`. Wiring into `Player` is the next slice.
- A web item is `Full` mode only (no hidden or audio-only playback; YouTube developer policies).
- Autoplay only when the host asks (`autoplay: true`).
- Premium no-ads could not be shown in the spike (no ads in any run, signed in or out): re-check in the real app.

## Manual smoke
Needs the WebView2 runtime and a network connection. Use a throwaway host (for example a small WPF window that
hosts `WebMediaView`) and a user data folder under `work\fatzmedia\Video`.
1. `await view.OpenAsync("https://youtu.be/<id>")` loads the item: `Duration` is set, `State` is `Cued`, `AvailableRates` is filled, nothing plays.
2. `PlayAsync` plays (state `Playing`, `PositionChanged` about four times a second); `PauseAsync` pauses; `SeekAsync(60 s)` jumps; `SetVolumeAsync(0.3)` and `SetMutedAsync` change `Volume` and `IsMuted`; `SetRateAsync(1.5)` works, `SetRateAsync(1.3)` throws.
3. Open an item that disallows embedding: `Error` raises `EmbeddingNotAllowed`, the host shows the message.
4. `SignInAsync`, sign in on Google's page, `OpenAsync` again, restart the host with the same folder: still signed in. `SignOutAsync` clears it.
5. Open a playlist address: `MediaInputSource.Parse` throws `NotSupportedException` with the reason, and an ordinary `https://...mp4` address still plays through network playback.
6. With a Premium account signed in, play a normally ad-supported video and compare with signed out (spike: inconclusive).
