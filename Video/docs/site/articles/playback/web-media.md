# Web media (YouTube)

`FatzMedia.Video` can play a YouTube link through the player's normal controls. It does not download or decode
the video. A web view shows YouTube's own embedded player, and the `Player` drives it: `Load`, `Play`, `Pause`,
`Stop`, `Seek`, `Position`, `Duration` and the volume work, and the usual events are raised.

> [!NOTE]
> Web media is built and checked on Windows (WPF, WinForms and Avalonia). macOS and Linux Avalonia hosts are not
> verified yet. YouTube is the only service. Other services (Vimeo, Dailymotion, Spotify, Deezer) may follow one at a
> time.

## What you need

| You are using | Package and control | Web view |
| --- | --- | --- |
| WPF | `FatzMedia.Video.WebView2`, `WebMediaView` | WebView2 |
| WinForms | `FatzMedia.Video.WebView2`, `WebMediaControl` | WebView2 |
| Avalonia | `FatzMedia.Video.WebView2.Avalonia`, `WebMediaView` | Avalonia's `NativeWebView` (WebView2 on Windows) |

On Windows the WebView2 runtime must be installed; it comes with Windows 11 and current Edge. On Windows an Avalonia
host also needs an application manifest with the Windows 10 `supportedOS` entry, or Avalonia cannot create the web
view's native window.

## Playing a link through the player

Put the web control somewhere in your window, start it, and attach its player to your `Player`. After that,
`Load` and `LoadAsync` accept a YouTube address as well as a file path.

```csharp
var web = new WebMediaView();            // WPF; WebMediaControl in WinForms
layoutRoot.Children.Add(web);
await web.InitializeAsync();             // not needed in Avalonia: attach once the control is in the visual tree

player.AttachWebMediaPlayer(web.Player);

await player.LoadAsync("https://youtu.be/<video id>");
await player.PlayAsync();
```

- A string is read as a web address only when a supported service recognises it. **Every other string is a file path
  exactly as before.** Loading a file afterwards ends the web item.
- An address that names no single video (a playlist, a channel, a search) fails with a `MediaError`
  (`UnsupportedFormat`, with the reason). Nothing is played.
- Without an attached web player, a web address raises `MediaErrorCode.WebMediaUnavailable`.
- `player.IsWebMedia` tells you that a web item is the current one.
- The picture is the web control itself; a web item is not drawn by a renderer. Background and Audio modes reject web items.
- The synchronous calls never wait for the web view. `Load`, `Play`, `Pause` and `Seek` issue the command and return
  at once, even on the UI thread, and the answer comes as events (`MediaLoaded`, `MediaStarted`, `MediaPaused`,
  `MediaEnded` and so on). `LoadAsync` completes when the item is ready. A `Play` or `Seek` issued while the item is
  still loading is remembered and applied when it is ready.
- Looping works with `LoopMedia`.

### What does not apply to a web item

YouTube's player makes the sound, so FatzAudio is idle for a web item. Pitch, tempo, the equalizer, fades,
normalization, mixing, prepared-next, extraction, background playback and the CD+G overlay do nothing.
`WebMediaSupport.Describe(feature)` gives the reason for each. Volume works.

## Using the control by itself

The control also plays without a `Player`:

```csharp
await web.OpenAsync("https://youtu.be/<video id>", autoplay: true);
IWebMediaPlayer playback = web.Player;   // transport, position, volume and events
```

`UserDataFolder` is the web view's profile folder; set it before `InitializeAsync` if you want it somewhere other
than the default under the user's local application data. Cookies and the sign-in live there.

## Full screen and a second screen

YouTube's own player only fills the web view, so the controls do the full screen themselves. `EnterFullScreen()` moves
the one web view into a borderless window that covers a whole screen, and `ExitFullScreen()` brings it back. It is
the same web view, so the picture, sound and position carry on. Pass a screen's bounds to put it on a second monitor:

```csharp
web.EnterFullScreen(secondScreen.Bounds);   // WPF/WinForms: System.Drawing.Rectangle in pixels; Avalonia: PixelRect
```

On Windows a double-click on the video raises `DoubleClicked` and, by default, toggles this full screen; Esc leaves it.
A host with its own full screen (the test players) sets `DoubleClickTogglesFullScreen = false` and handles the event.
The control reads the mouse with a read-only hook, so nothing is drawn over YouTube's player, links or ads and the
click still reaches YouTube. In the test players the second-screen button and the double-click do this for a
YouTube item. YouTube's own full-screen button stays inside the web view.

## Signing in

`IWebMediaPlayer.SignInAsync` opens Google's own sign-in page in the same web view and profile, and `SignOutAsync`
clears the profile. FatzMedia never sees a password or a token. A signed-in profile keeps YouTube's account
settings. Whether YouTube Premium removes ads in an embedded player is **unverifiable here**: the embedded player
served no pre-roll even to a signed-out viewer on the owner's network, so an ad-free result proves nothing.

## Why the player page has a web address

YouTube answers error 153 to an embed that has no real web origin. The WebView2 adapters serve the player page from a
virtual https host name (`https://web.fatzmedia.test/`) and the Avalonia adapter from a tiny loopback server on
`127.0.0.1`. You do not need to do anything; it explains the address in the web view's network log.

## Errors

A video that cannot play raises `MediaError` with the reason (`WebMediaErrorKind`): the video is private or not found,
its owner does not allow embedding, YouTube could not verify the page's origin (error 153), or the player failed.
Many big music videos are not embeddable (YouTube errors 101 and 150: "The owner of this video doesn't allow it to play
in other apps"); movie trailers and many other channels are, so use those to try the player.

## Limits set by YouTube's developer policies

No extraction, saving, caching or playing with the player hidden. These are not settings; the API has no way to ask
for them.

## See also

- [Loading media](loading-media.md)
- [Transport](transport.md)
- `Video/docs/WEB_PLAYER_DESIGN.md` in the repository for the design and the stages (060A).
