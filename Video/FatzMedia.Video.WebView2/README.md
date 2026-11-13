# FatzMedia.Video.WebView2

Plays online media (YouTube first, stage 060A) in a Windows web view through the service's official embedded
player. Core (`FatzMedia.Video`) only recognises the address and defines `IWebMediaPlayer`; this adapter owns the
web view.

- `WebView2MediaPlayer` implements `IWebMediaPlayer` on a `CoreWebView2` (any host: WPF, WinForms).
- `WebMediaView` is the WPF control: a `WebView2` plus a ready-made player.
- Sign-in: `SignInAsync` opens the service's own sign-in page in the same persistent profile. FatzMedia never
  sees a password or a token. `SignOutAsync` clears the profile.
- A web item takes over the media surface: FatzAudio routing, key and tempo, mixing and the CD+G overlay are not
  supported. No extraction, caching or background playback (YouTube developer policies).
- The player page is served from a virtual https host name (`https://web.fatzmedia.test/`); without a real https
  origin YouTube answers error 153.

Needs the WebView2 runtime (installed with Windows 11 and current Edge).
