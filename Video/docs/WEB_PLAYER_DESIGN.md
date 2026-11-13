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
- Videos that disable embedding report an error: show a clear message.
- "Made for Kids" videos change what tracking is allowed.
- Autoplay only when the user chose to start playback.

## Effort (rough)

About 1 to 2 weeks after the spike for YouTube: core contract, bridge, WPF, WinForms and Avalonia
adapters, tests, referrer fix. Each later service about 2 to 3 days (Vimeo, Dailymotion); Spotify about a
week plus reading its rules.
