# Stage 060A slice 5: a YouTube link box in the test players

Status: implemented; smoke-checked on Windows (see below), pending the owner's own click-through.

## What changed
- The WPF, WinForms and Avalonia test players have a **YouTube** row in the **Next** flyout (WinForms: the **Next**
  tab): a text box, **Open**, **Sign in** and **Sign out**, and a one-line status. Enter in the box also opens.
- The logic is in the shared `PlayerViewModel` (`PlayerViewModel.WebMedia.cs`): `WebLink`, `OpenWebLinkCommand`,
  `WebSignInCommand`, `WebSignOutCommand`, `WebInfoText`. The host supplies its web view through the new
  `IPlayerWebView` (`PlayerHost.WebView`): `EnsureReadyAsync()` starts the web view and returns its player,
  `SetVisible(bool)` shows or hides it. The view model attaches that player to the engine
  (`PlayerCore.AttachWebMediaPlayer`) and then calls `PlayerCore.LoadAsync(text)`, so the box exercises exactly the
  `Player.Load(url)` path of slice 4. Hosts keep view code only.
- The web view **takes over the media area**: WPF (the surface is hidden, the web view shown in the same cell),
  WinForms (the two share a panel; one is visible), Avalonia (the native web view covers the surface). A local file
  loaded afterwards ends the web item and the engine surface comes back (`MediaLoaded`/`MediaClosed` with
  `IsWebMedia` false hide the web view).
- A text that is not a YouTube address is refused in the box ("Not a YouTube link."): the box never turns typed text
  into a file path. A playlist, channel or search link goes through `LoadAsync` and shows the engine's error text.
- The test players keep the web profile (the Google sign-in) in `%LocalAppData%\FatzMedia\SamplePlayer\WebView2`,
  not the library default folder. `--web-profile=<folder>` picks another one (tests use a scratch folder).
- The Avalonia test player has an `app.manifest` with the Windows 10 `supportedOS` entry. Without it the native web
  view crashes the process ("Unable to create child window for native control host", see the
  `FatzMedia.Video.WebView2.Avalonia` README).
- The boundary script's project DAG lists the three new test-player references to the WebView2 packages;
  `check-presentation-parity.ps1` requires the five new members in all three players.

## Tests
`PlayerViewModelWebLinkTests` (12): no host web view, command states, open (attach, show, load, autoplay), a second
link, refused texts, a playlist, a local file after a web link (and back), a web view that cannot start, an embedding
error during the load, sign in/out, detach on dispose. `PlayerStartupOptionsTests.Parse_WebProfile_...`.

## Manual smoke (Windows, WebView2 runtime, network)
Start a test player (add `--web-profile=<a scratch folder>` to keep your own sign-in out of it), then:
1. Open the **Next** flyout (WinForms: the **Next** tab). In the **YouTube** box paste `https://youtu.be/dQw4w9WgXcQ`
   and press **Open**: the video plays over the picture area, the seek bar and times follow it, **Pause**/**Play**/
   **Stop** and the volume slider work. (Avalonia loads from a local loopback page: if YouTube says the owner does not
   allow it, try another video, for example `https://www.youtube.com/watch?v=aqz-KE-bpKQ`.)
2. Open another YouTube link: it replaces the first. Type `song.mp3` and Open: "Not a YouTube link", nothing changes.
3. Paste a playlist link: the status line shows the playlist message and the playing video is not disturbed.
4. **Open...** a local file (or drop one): it plays in the engine and the web video stops, the picture area is the
   engine's again. Open a YouTube link again: the web view returns.
5. **Sign in**: Google's page shows in the web view; sign in, then **Open** the link again (the Premium no-ads
   re-check from slice 4). **Sign out** clears the web view's cookies.
6. Close the window while a web video plays: no crash, no sound left running.

## Checked on Windows (2026-11-18, scratch profiles)
WPF, WinForms and Avalonia test players, driven through UI Automation: a YouTube link plays and the seek bar follows
(WPF, WinForms, Avalonia with `aqz-KE-bpKQ`); in WPF a second link replaces the first, a playlist link leaves the
playing video alone and shows the reason, and a local file takes the picture area back. Not driven: sign in/out,
and the local-file swap in WinForms and Avalonia (the file dialog would not take scripted keystrokes).
