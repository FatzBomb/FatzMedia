# 060A spike results (2026-11-13)

Throwaway WPF app: `web-spike\app` (WebView2 1.0.3485.44, runtime 158.0.4370.16 beta, .NET 10). Profile: `web-spike\profile`. Logs: `web-spike\logs\run-20261113-150646.log`. Test video: YouTube's own IFrame API demo (`M7lc1UVf-VE`), muted, on screen only. Nothing extracted, cached or captured. FatzMedia repo untouched.

| # | Item | Result |
|---|---|---|
| 1 | Video plays from a virtual https host, no error 153 | **PASS** |
| 2 | IFrame API control from C# over WebView2 messaging | **PASS** |
| 3 | Google sign-in allowed in the embedded view; cookies persist between runs | **PASS** (sign-in accepted, no security block; persisted across a restart) |
| 4 | Premium no-ads in the embed | **INCONCLUSIVE / untestable here** (not a pass): no ads in any run, signed in or out, on several videos and a free movie, so nothing is proven |
| 5 | Avalonia NativeWebView | **NOT TESTED** (docs only; Windows test skipped by default, needs Avalonia's WebView package, no answer from the owner); see below |

## 1. Virtual host and error 153
`SetVirtualHostNameToFolderMapping("spike.fatzmedia.test", wwwroot, Allow)`, page loaded as `https://spike.fatzmedia.test/player.html`.

| Variant | Result |
|---|---|
| A virtual https host, `playerVars.origin` = page origin | ready, PLAYING |
| B virtual https host, no `origin` param | ready, PLAYING |
| C virtual host, `<meta name=referrer content=no-referrer>` | ready, PLAYING |
| D virtual host, `strict-origin-when-cross-origin` | ready, PLAYING |
| E `NavigateToString` (origin `null`, about:blank) | ready, then **error 153**, never plays |

So the fix is just serving from a real https host name; the variant with no origin works too. E confirms the error 153 problem and that the virtual host cures it. Note `document.referrer` of the host page is empty in every case (a top-level load has no referrer); the embed's Referer is the host page's origin, which is what YouTube checks. Prefer variant A or B (the default policy); no special referrer tweak was needed.

## 2. IFrame API control
Over `PostWebMessageAsJson` (C# to page) and `chrome.webview.postMessage` (page to C#), all worked in variant A:
- play: state -1, 3 (buffering), 1 (playing).
- seek 60 s: state 3 then 1, t=60.2.
- volume 30: query reported vol 30. Mute worked.
- rate 1.5: `onPlaybackRateChange` fired; `getAvailablePlaybackRates` = 0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2.
- pause: state 2.
- duration 1344 s; events: ready, state, rate, error, apichange.
Time to ready about 0.5 s, to playing about 1 s.

## 3. Google sign-in and cookies
The owner signed in inside the embedded view (no popup; same view). Google accepted it with no "browser not secure" block. Navigation log: sign-in page, then a passkey-enrollment prompt, a Google verification page, `youtube.com/signin_prompt`, then `youtube.com/?pli=1` (signed in). Note: the first UI had the toolbar's player controls out of reach, so the owner could not return to the player; the toolbar now has big BACK TO PLAYER and UNMUTE + PLAY buttons.

Persistence: after closing and reopening the app with the same user-data folder, the profile still held the Google/YouTube session cookies (names only checked, never values: SID, SAPISID, LOGIN_INFO, __Secure-1PSID/3PSID and others, expiring 2027-11/12), and youtube.com showed the signed-in avatar with no "Sign in" link. FatzMedia saw no password or token. Persistent WebView2 profile = sign-in kept.

Not yet checked: that the embedded player (iframe on the virtual host) uses this session. That is item 4.

## 4. Premium no-ads
The owner saw **no ads** on YouTube's own IFrame API demo video while signed in with Premium. That alone proves nothing: that video may show no ads even signed out. **"No ads with Premium" is only proven if the signed-out state shows ads for the same video.** Second test run 2026-11-13 (log `run-20261113-151623.log`): Rick Astley, dQw4w9WgXcQ, signed in with Premium: no ad. Then SIGN OUT / CLEAR PROFILE (logged 15:23:12, profile cleared, no sign-in afterwards) and the same video again: **still no ad** (the owner: no ads signed in or out). A video with no ads in the signed-out state cannot show that Premium removes them, so item 4 stays **INCONCLUSIVE**. Possible causes: that video, the owner's region or network, or a fresh embed profile not being served ads. The owner also tried a free-with-ads movie (his idea): no ads either. Further candidates given to the owner: JGwWNGJdvx8, 9bZkp7q19f0, kJQP7kiw5Fk, and a signed-out private browser window to check he gets ads at all. Re-check item 4 in the real app, or here with a video that shows ads signed out. Original plan: a popular, normally monetized video, first signed out (after SIGN OUT / CLEAR PROFILE), then signed in with Premium, and report pre-roll and mid-roll ads in each state.

Manual run by the owner: Play, Pause, Volume and Rate worked; "Mute" and "Seek 60s" seemed to do nothing. Cause of Mute: the first toolbar had only an Unmute button, no Mute button (a spike UI gap, not an API problem). Seek: I added a Mute button and a `--btntest` mode that presses the real toolbar buttons through UI Automation (same click handlers). Result: mute+play gave muted=true; the Seek 60s button moved the player from about 4 s to 62.8 s; Unmute gave muted=false. So both work through the buttons and the API. the owner's seek press left no event in the log; most likely it was pressed while the player page was being reloaded by the auto tests, but this was not reproduced. The on-screen "video player configuration error" The owner saw was the deliberate variant E (NavigateToString, error 153) at the end of "Run auto tests"; he recovered with BACK TO PLAYER.

## 5. Avalonia NativeWebView (documentation only; not run)
- Windows: WebView2 (legacy WebView1 fallback on old Windows 10).
- macOS and iOS: WKWebView.
- Linux: WPE WebKit if installed, else WebKitGTK (4.1 / soup-3 recommended; GTK 4 build WebKitGTK 6.0 not supported). WPE is not in Ubuntu since 22.10. An older XPF page says embedded NativeWebView is not supported on Linux (use NativeWebDialog); the newer table and 11.4.0 notes say it is.
- Not tested: loading the same page and JS messaging with Avalonia on Windows. Would need Avalonia's WebView package in the throwaway project (new dependency, needs OK). Mac and Linux cannot be tested in this environment.
- Risk for the design: YouTube behaviour (Premium, sign-in, 153) must be re-checked per engine; WKWebView and WebKitGTK are not Chromium.

Sources: https://docs.avaloniaui.net/accelerate/components/webview/nativewebview, https://docs.avaloniaui.net/xpf/embedding/web-view

## Summary and notes for stage 060A
- Pass: 1 (virtual https host, no error 153), 2 (full control and events over WebView2 messaging; seek, mute, volume, rate and pause also confirmed through the real toolbar buttons), 3 (Google sign-in accepted in the embedded view with no security block; the login persisted across a restart in the persistent profile).
- Inconclusive: 4 (Premium no-ads); re-check in the real app, or with a video that shows ads signed out.
- Not tested: 5 (Avalonia NativeWebView; Mac and Linux cannot be tested here). Docs say WebView2 / WKWebView / WPE WebKit or WebKitGTK; YouTube behaviour must be re-checked on each non-Chromium engine.
- Error 153 appears only without a real https origin (variant E). Serve the page from a virtual https host; no referrer tweaks needed.
- `Player.Open(url)` will detect YouTube hosts first (`youtube.com`, `www.`, `m.`, `youtu.be`; ids from `watch?v=`, `youtu.be/`, `embed/`, `shorts/`) and send every other URL on to the existing network playback (039A) unchanged. Playlist, channel and search links return a clear "not supported" error.
- `controls=0` can hide YouTube's own play bar; title, logo, links, ads and suggestions stay and must not be covered.
- Position is polled (about 200 to 250 ms) and is not frame-accurate; during ads position and duration can describe the ad.
- Spike hygiene: no password or token seen or stored by the spike (cookie names only logged); nothing extracted, cached or captured; FatzMedia repo untouched.
