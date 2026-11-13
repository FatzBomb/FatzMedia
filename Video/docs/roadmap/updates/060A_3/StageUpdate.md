# Stage 060A slice 3: Avalonia web media control

Status: implemented on Windows, pending the owner's manual smoke. macOS and Linux untested.

## What changed
- New project `FatzMedia.Video.WebView2.Avalonia` (`net10.0`, references Core and Avalonia only):
  `AvaloniaWebMediaPlayer` (`IWebMediaPlayer` on Avalonia's `NativeWebView`), the Avalonia `WebMediaView`
  control, and `LoopbackPageServer`.
- **New package, owner approved 2026-11-13: `Avalonia.Controls.WebView` 12.1.2** (Avalonia's own web control;
  MIT licence, $0, no account or licence key; needs Avalonia 12.0 or newer, the repo uses 12.1.4). Avalonia's paid
  tiers cover other components, not this one. Fallback if ever needed: the community `NativeWebView` (MIT).
- The YouTube player page and the message parser are shared with `FatzMedia.Video.WebView2` by file link. The page
  now posts through `chrome.webview` when present, else through `invokeCSharpAction(json)` (Avalonia), and accepts
  commands as a WebView2 message or `fmCommand(json)`.
- Tests: `WebView2AvaloniaLoopbackServerTests`. Boundary scripts know the new project.

## Decisions
- `NativeWebView` cannot serve a page from a virtual https host (no documented response interception), and a page
  with no origin gets YouTube error 153. So the player page is served from a loopback server on `127.0.0.1` (a real
  origin and Referer). Checked on Windows: no error 153.
- Sign-in uses the web view's own profile (`SignInAsync` opens Google's sign-in page). The web view's profile and
  whether the sign-in survives a restart are not controlled by us here: Avalonia's `NativeWebView` documents no user
  data folder; to be checked in the smoke. `SignOutAsync` deletes the web view's cookies (where the platform
  offers a cookie manager).
- The Windows host needs an application manifest with the Windows 10 `supportedOS` entry (see the project README).

## Verification so far (scratch Avalonia host on Windows, against YouTube)
Loaded (duration 1344 s, speeds 0.25 to 2), muted, played, seeked to 60 s, volume 0.3, speed 1.5, paused; state and
position events arrived. The WPF adapter was re-run against the changed page: unchanged behaviour.

## Manual smoke
Windows, with the WebView2 runtime, in an Avalonia host that has the manifest:
1. Put a `WebMediaView` in a window; once open, `await view.OpenAsync("https://youtu.be/<id>")`: duration set,
   state `Cued`, nothing plays.
2. Play, pause, seek, volume, mute, speed 1.5 work; a video that disallows embedding raises `EmbeddingNotAllowed`.
3. `SignInAsync`, sign in on Google's page, `OpenAsync` again, restart the host: is the sign-in still there?
   (Record the answer: it decides whether the Avalonia control needs a profile setting.) `SignOutAsync` clears it.
4. macOS and Linux: not tested; try when a machine is available (WKWebView / WebKitGTK may behave differently).
