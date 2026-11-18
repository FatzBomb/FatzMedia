# Stage 060A slice 6: full screen and a second screen for the web player (WPF control)

Status: implemented for the WPF `WebMediaView`, pending the owner's manual smoke. The test players' wiring, the WinForms
and the Avalonia controls follow (see Not done).

## The problem
In the test player a double-click on a normal video toggles full screen. With a YouTube item it did not: the web view
runs in its own browser process and takes the mouse there, YouTube's player in the page does not request full screen from
a double-click under real input, and the host never hears the click. The first click paused and the second resumed.
The second-screen button also had nothing to move: a web item has no media surface.

## What changed (`FatzMedia.Video.WebView2`, WPF `WebMediaView`)
- `EnterFullScreen(Rect? bounds)` / `ExitFullScreen()` / `IsFullScreen` / `FullScreenChanged`: the control moves its one
  web view into a borderless, topmost window that covers `bounds` (device-independent pixels; default: the whole screen the
  control's window is on). The same call puts the video on a **second monitor**: pass that screen's bounds. It is one web
  view, never a second copy, so the picture, sound and position are the same. Verified: playback continued across the move.
- `DoubleClicked` event and `DoubleClickTogglesFullScreen` (default true): a **low-level mouse hook** reads left-button
  presses over the web view's rectangle and raises `DoubleClicked`; by default the control toggles its own full screen.
  A host with its own full screen (the test player) sets `DoubleClickTogglesFullScreen = false` and handles the event.
  The hook only reads: nothing is drawn over YouTube's player, links or ads, and the click still reaches YouTube.
- Esc leaves the control's full-screen window (a low-level keyboard hook that reads Esc, active only while that window is
  open).
- `AutoFullScreen` (default true) and `FullScreenRequested`: if the page itself asks for full screen (YouTube's full-screen
  button), the control goes full screen too; set `AutoFullScreen = false` to handle it yourself.
- `DoubleClickDetector` (pure logic) with tests.

## Decisions and limits
- The hook is system-wide but read-only and checks one rectangle. It needs the UI thread's message loop (WPF has it).
- YouTube's own double-click still toggles play and pause twice (net: unchanged) while the host goes full screen. A video
  that was paused stays paused.
- A short pause/buffer blip shows in the state events while the web view moves between windows; playback continues.
- The mouse hook is Windows only (this is the WPF/WebView2 adapter). Esc and double-click are not available from the
  Avalonia control (no web-view mouse hook there yet).

## Manual smoke
1. WPF host with a `WebMediaView` playing a YouTube item: double-click the video: it goes full screen on the screen the
   window is on; sound and position continue; double-click again, or press Esc, to come back.
2. `view.EnterFullScreen(secondScreenBoundsInDips)`: the video appears full screen on the second monitor and keeps playing;
   `ExitFullScreen()` returns it.
3. YouTube's own full-screen button also makes the control full screen (`AutoFullScreen`).
4. With `DoubleClickTogglesFullScreen = false`, a double-click raises `DoubleClicked` and nothing else moves.

## Not done in this slice
- Test players: the second-screen button for a YouTube item and the double-click mapped to the player's own full screen
  (`FullScreenToggleRequested`). They wait for PR 40 (the link box) and then use `DoubleClicked` and
  `EnterFullScreen(bounds)`.
- WinForms `WebMediaControl` and the Avalonia control.
