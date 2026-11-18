# Web media player: design

Status: draft; nothing is built. Stage `roadmap/future/060A_IDEA_WebMediaPlayer.md`. Owner 2026-11-13.

## Goal

`Player.Open(url)` plays a stream from an online service in a system web view, using the service's
official embedded player. It is part of FatzMedia.Video, not a separate project. Users find content
themselves and paste a URL; there is no search or browsing.

## Release scope

Release: **YouTube only**, on Windows (WPF, WinForms) and Avalonia first. Other platforms later.

| Service | Stage | Route |
|---|---|---|
| YouTube | Release | Official IFrame Player API in a web view |
| Vimeo | Later | Official Player SDK |
| Dailymotion | Later | Official Player SDK |
| Spotify | Later | Official embed or playback SDK; user signs in (Premium for full playback) |
| Deezer | Later | Unverified: check whether new developer apps are open before planning |
| KaraFun | Later, blocked | Catalog and playback API is an OEM partner deal. No `.kfn` decoder (protected format; their terms forbid getting around it) |

## Shape

- **Core:** a small provider contract (a service's id, URL detection and the commands and events a web
  item supports) and URL detection. No WebView reference, no `[DllImport]`; the boundary scripts
  (`check-core-boundary.ps1`) stay green.
- **Adapters** supply the web view and only create it and pass messages: WebView2 (WPF, WinForms),
  Avalonia `NativeWebView`; later WKWebView (macOS, iOS) and Android WebView. No player logic.
- **Bridge:** one small JavaScript page per service plus a message channel to Core. The same bridge can
  later run in a plain browser page.
- A second screen or fullscreen is the host placing the web view in its own window.

## What a web item can and cannot do

Supported (the official player gives it): load by URL, play, pause, seek, volume, mute, state events
(ready, playing, paused, ended, error), duration and position, fullscreen, the speeds the player offers.

A web item takes over the media surface. These report `NotSupported`, never fail silently:
- FatzAudio routing: no key change, fine tempo, mixing, crossfade or recording.
- The CD+G overlay and other Video overlays on the web item.
- Extracting, downloading, caching or saving audio or video.
- Background or audio-only playback with the player hidden.
- Hiding, overlaying or altering the player, its links or its ads.
- Search, browse, playlists, queues.

Why: YouTube's developer policies forbid separating or modifying audio or video, caching, background
playback, and obscuring the player or ads. Re-check the exact wording before release. Embedded players
must be at least 200 by 200 px; 480 by 270 is recommended for 16:9.

## Sign-in

- `SignIn` / `SignOut` open the service's login in the same persistent web view profile as playback.
  The user types credentials into the service's own page.
- FatzMedia never sees, stores or logs passwords or tokens. Cookies live in the web view profile;
  `SignOut` clears them.
- YouTube public videos need no sign-in. Spotify and KaraFun would.

## First task: spike

Before any API is final, prove in a throwaway host:
1. Google sign-in works inside the embedded view.
2. YouTube Premium's no-ads applies to the embed.
3. Sign-in cookies persist and work in the embed.
4. The Referer / error 153 fix: YouTube rejects embeds sent without a Referer, so the player page is
   served from a virtual https host name (WebView2 can map a host name to a folder).
5. Avalonia `NativeWebView` coverage on macOS and Linux.

The results decide the release scope and the adapter plan. Not all of them may pass.

## Other risks

- Ads cannot be skipped or hidden; the host UI should expect them.
- Videos that disable embedding report an error (YouTube 101 or 150): the player says "The owner of this video doesn't allow it to play in other apps". Most major-label music videos are like this; movie trailers and many other channels embed.
- "Made for Kids" videos change what tracking is allowed.
- Autoplay only when the user chose to start playback.

## Findings (2026-11-13 to 2026-11-18)

- **Spike (Windows, WebView2):** a page served from a virtual https host plays with no error 153; the IFrame API controls
  play, pause, seek, volume, mute, speed and state events from C#; a Google sign-in works in the embedded view and survives
  a restart. A page with no real origin (for example `NavigateToString`) gives error 153.
- **Premium no-ads: unverifiable here.** The owner saw no ads in the embedded player, signed in with Premium or signed
  out, on several videos and a free movie. A later check from the owner's network: a signed-out youtube.com page showed an
  ad (so ads reach that network), but the embedded player served **no pre-roll** signed out (movie trailers played
  straight through). So an ad-free result signed in proves nothing, and Premium behaviour in the embed cannot be verified
  by looking for ads. Recorded as an open, unverifiable item; the part we control (sign-in in the persistent profile) works.
- **Embedding:** many big music videos (Despacito, Gangnam Style, Shape of You, See You Again, Bohemian Rhapsody) return
  error 150, the owner disallows embedding, so they cannot play in any host. Choose trailers or other embeddable videos for tests.
- **Full screen and second screen:** the web view runs in its own browser process, so a double-click on the video never
  reaches WPF and YouTube's player does not enter full screen from it. The WPF control observes the double-click with a
  read-only hook and moves its one web view to a borderless full-screen window on any screen (`updates/060A_6`).

## Effort (rough)

About 1 to 2 weeks after the spike for YouTube: core contract, bridge, WPF, WinForms and Avalonia
adapters, tests, referrer fix. Each later service about 2 to 3 days (Vimeo, Dailymotion); Spotify about a
week plus reading its rules.
