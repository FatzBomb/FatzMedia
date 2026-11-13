# FatzMedia.Video.WebView2.Avalonia

Plays online media (YouTube first, stage 060A) in an Avalonia window through the service's official embedded
player. Core (`FatzMedia.Video`) recognises the address and defines `IWebMediaPlayer`; this adapter owns the web
view, Avalonia's `NativeWebView` (package `Avalonia.Controls.WebView`, MIT).

- `AvaloniaWebMediaPlayer` implements `IWebMediaPlayer` on a `NativeWebView`.
- `WebMediaView` is the Avalonia control: a `NativeWebView` plus a ready-made player.
- The player page is served to the web view from a tiny loopback server (`http://127.0.0.1:<port>`), because
  YouTube rejects an embed that has no real origin (error 153).
- Sign-in opens the service's own page in the same web view; FatzMedia never sees a password or token.
- Web items take over the media surface: no FatzAudio routing, key, tempo, mixing or CD+G overlay.

## Using it
- Add the control to a window, then `await view.OpenAsync("https://youtu.be/<id>")` once the window is open (the
  web view creates its native window when it joins the visual tree).
- On Windows the host needs an application manifest with the Windows 10 `supportedOS` entry, or Avalonia cannot
  create the native child window ("Unable to create child window for native control host").
- Needs the platform web engine: WebView2 on Windows, WebKit on macOS and Linux.
- Verified on Windows only (stage 060A slice 3). macOS and Linux are untested.
