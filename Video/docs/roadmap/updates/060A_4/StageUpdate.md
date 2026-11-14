# Stage 060A slice 4: YouTube links through Player.Load

Status: implemented, pending the owner's manual smoke.

## What changed
- `PlayerCore.AttachWebMediaPlayer(IWebMediaPlayer?)` (new file `Api/PlayerCore.WebMedia.cs`): the host attaches
  the player of its web view (`WebMediaView.Player` in the WPF, WinForms or Avalonia control, after its
  `InitializeAsync` or once the Avalonia control is in the visual tree). The host keeps ownership.
- `Load(string)` and `LoadAsync(string)` (every overload that took a path) now read the text with
  `WebMediaProviders`: a YouTube address becomes a web source; **every other string is a file path exactly as
  before** (an `https://` address that is not YouTube is still treated as it was, a file path). An address of a
  supported service that names no single video (playlist, channel, search) fails with `MediaError`
  (`UnsupportedFormat`, with the reason) and never reaches the engine or network playback. `Load(MediaInputSource)`
  accepts `MediaInputSource.Parse`/`FromWebMedia` sources too. Explicit `FromUri` network sources are unchanged.
- While a web item is active: `Play`, `Pause`, `Stop`, `Seek`, `SeekAsync`, `Position`, `Duration`, `State`,
  `IsOpen`, `IsWebMedia` and `Effects.Volume` act on the web item, and `MediaLoaded`, `MediaStarted`,
  `MediaPaused`, `MediaEnded`, `MediaStopped`, `MediaClosed`, `MediaLooped` (with `LoopMedia`), `StateChanged`,
  `PositionChanged`, `SeekCompleted` and `MediaError` are raised. `PlayOnLoad` starts it.
- Loading a file, `Close`, `Reset`, `Dispose` or detaching ends the web item (the web view is paused).
- Without an attached web player a web address raises `MediaErrorCode.WebMediaUnavailable`. Background and
  Audio modes reject web items.
- The WebView2 and Avalonia web players now marshal their own calls to the UI thread (a call from Core's control
  queue, a pool thread, is posted, never run on the wrong thread).

## The deadlock rule
The web view lives on the host's UI thread, and a synchronous `Load` or `Pause` blocks the calling thread. So a web
item never waits for the web view: the synchronous calls issue the command (the web player posts it to the UI
thread) and return `true`; the answer arrives as events. `LoadAsync` completes when the item is ready. Tests hold the
web player's commands pending and show the synchronous calls still return.

## Not supported for a web item (the engine is idle)
Pitch, tempo, equalizer, fades, normalization, CD+G, prepared-next, mute (use the web player), key and tempo:
`WebMediaSupport.Describe` gives the message. They do not raise errors; they simply do not apply.

## Tests
`PlayerCoreWebMediaTests` (22): routing, unsupported addresses, no-player error, the no-wait rule for load and
controls, state and event mapping, looping, error handling, volume, modes, and that file paths, other URL strings,
explicit network sources and a file loaded after a web item go to the engine exactly as before. The whole Windows
suite (1568) and the portable suite (1714) pass.

## Manual smoke
Windows (WPF host is enough), WebView2 runtime, network:
1. Create a `Player`, a `WebMediaView`; `await view.InitializeAsync()`; `player.AttachWebMediaPlayer(view.Player)`.
2. `player.Load("https://youtu.be/<id>")` on the UI thread (synchronous): the window does not freeze; `MediaLoaded`
   arrives with the duration. `player.Play()`, `Pause()`, `Seek(60)`, `Stop()`, `Effects.Volume = 0.3` work and the
   seek bar (`Position`/`Duration`, `PositionChanged`) follows the video.
3. `player.Load("song.mp3")` (a local file) plays through the engine; the web video stops.
4. `player.Load("https://www.youtube.com/playlist?list=...")` raises `MediaError` with the reason and nothing plays.
5. Same steps with the WinForms control (`WebMediaControl`) and the Avalonia control.
6. Premium no-ads re-check (spike was inconclusive): pick a video that shows an ad when signed out; play it signed
   out, then `SignInAsync`, sign in on Google's page, `Load` again and compare.

## Follow-up fix (found by the launcher self-test, 2026-11-14)
- **Controls before ready:** a `Play`, `Pause`, `Stop` or `Seek` issued while the web item was still loading (for
  example `Load(url)` followed at once by `Play()`) was lost, because the page ignores commands until it is ready.
  They are now remembered and applied when the item is ready (a later Pause cancels a pending Play). Tests added.
- **Autoplay policy:** WebView2 blocks playback that did not start from a user gesture inside the page, and the host
  starts playback from its own controls. The WPF and WinForms controls now create the web view with
  `--autoplay-policy=no-user-gesture-required` (`WebView2MediaPlayer.EnvironmentOptions()`), and the Avalonia
  control sets the same argument. Without it an unmuted `Play()` ended in `Stopped`.
- **Avalonia sign-in persistence:** the Avalonia control now has `UserDataFolder` (WebView2 profile folder, set
  through the package's `EnvironmentRequested`), so a sign-in can survive a restart like the WPF control's.
- Launcher self-test (a scratch WPF host with a real `Player`, a web view and a local mp3, Windows): 19 checks pass:
  YouTube through `Player.Load` (returns in 12 ms on the UI thread, MediaLoaded, Play, position advances, Seek,
  volume, Pause, Stop), a playlist link raises `MediaError`, a local file after a web item plays in the engine and the
  web item ends, YouTube again after a file, file again.
