# Completed Stage 051A - First-frame-ready signal and the A/B swap pattern

Prepared: 2026-11-06 (owner decision 2026-11-05, TriviaBomb: a WPF host replacing LibVLC).
Completed: 2026-11-07. Status: slice 1 done (PR #72, #73); owner smoke passed 2026-11-07 on the
desktop (`updates/051A_1`: 24 of 24 on the rerun of tests 1, 2 and 5; tests 3, 4 and 6 passed on
2026-11-06). No laptop run (owner decision): the comparison with the old trivia app stands.

## Why

A host that swaps clips without a black flash (TriviaBomb) keeps two players and loads the next clip
into the hidden one. It needs to know when that player's first frame is on its surface, so that it can
show it, hide the old one and change the rest of its screen in one UI-thread turn. Today it can only
guess (`PresentationFramePresented` is per session, on the render thread, every frame, and says nothing
about the surface's own bitmap or host window).

## Investigation (2026-11-06)

How each adapter learns that a load's first frame is on screen, before this stage:

| Adapter, path | Frame reaches the surface | Shown by | What a host could observe |
| --- | --- | --- | --- |
| WPF composited | renderer -> `WpfCompositedSurfaceHost.PresentFrame` (render thread, mailbox) -> `PublishPendingFrame` at `DispatcherPriority.Render` writes the `WriteableBitmap` | the tracker's `Changed` (render thread, after the sink call) -> `BeginInvoke` (Normal) -> `ApplyContentState` sets the host `Visible` | `ContentVisibilityChanged` (auto-hide only, a separate dispatcher operation from the bitmap write; Normal runs before Render, so the host can be shown before the bitmap holds the new frame) |
| WPF native | swap chain `Present` on the render thread; DWM shows it at the next vblank | the same tracker path shows the child HWND | the same; nothing with AutoHide off |
| Avalonia frame sink | `AvaloniaFrameSink.PresentFrame` -> `Publish` (Render priority) copies into the bitmap and invalidates | tracker `Changed` -> `UpdateContentState` (`_contentShown`) | `ContentVisibilityChanged` (auto-hide only) |
| Avalonia GPU shared image | `AvaloniaSharedImageSurface` pumps the image into a composition visual | `SetVisible(_contentShown)` | the same |
| WinForms native window | swap chain present | the first present after attach shows the child window (`_framePresentedSinceAttach`) | the same |
| WinForms frame sink | `WinFormsFrameSink` publish -> `Invalidate` + `Update` | `_framesShown` | the same |

Findings:

- Every renderer calls the frame sink **before** it raises `FramePresented` (Software, Vulkan and the
  D3D11 managed readback all call `PresentFrame` synchronously inside the present). So when the tracker
  learns of a load's first frame, that frame is already in the surface's mailbox (or published).
- `MediaClosed` of a replaced item is raised before the new item's `MediaLoaded`, so the tracker's
  latch (a frame presented before the load notice, a static image) holds only frames of the new item.
- **A hidden B surface is still presented to.** Core presents to every attached surface; the WPF
  composited dispatcher publish does not look at visibility; a hidden native child window is presented
  to as well. What breaks is size: a WPF `Visibility.Collapsed` (Avalonia `IsVisible = false`) surface is
  laid out at 0x0, the adapter resizes the session's surface to 1x1, and the first frame is rendered at
  1x1. The pattern therefore hides with `Visibility.Hidden` (WPF; keeps its layout size) or
  `Opacity = 0` (Avalonia), never `Collapsed`. WinForms `Visible = false` keeps the control's size.
- **`StartupMediaDelay` (500 ms, `PlayerViewModel`)**: its comment says it lets the surface bind to the
  new player first. The binding does not need it: `Player = player` is set on the UI thread, and WPF
  and Avalonia bindings and the WinForms binder update `MediaSurface.Player` synchronously inside that
  `PropertyChanged`. A load that still runs before the surface attaches (a surface not loaded yet, a
  WPF HWND not created yet) loses nothing: `AttachPresentationSurface` re-presents the current frame
  (`PresentAfterSurfaceChange("presentation surface attached")`), the content tracker arms for a session
  that is already open, and the device diagnostic is subscribed before the binding (the view model
  subscribes `MediaDiagnostic` before setting `Player`). The delay goes; a test loads before attach and
  checks the surface still gets the frame and the event.
- 047A needs a UI-thread cost number for composited mode: the bitmap write in `PublishPendingFrame` is
  the per-frame UI-thread work, and nothing measures it.

## Proposal (event name and exact timing)

**`MediaSurface.FirstFrameReady`** (`event EventHandler?`) on the WPF, Avalonia and WinForms
`MediaSurface`. "Ready" rather than "Shown": the surface has the frame and its presentation host is
visible, so if the host shows the surface (or hides another) in the handler, the new frame is what
appears; whether the host's own element is visible is the host's business.

- **Armed** once per load: each `MediaLoaded` of the bound session for media with visual content, and
  setting `Player` to a session that already has such media open. Nothing else arms it (play, pause,
  seek, loop, stop and play again, a mode switch, a resize, a re-attach, the second screen opening).
- **Raised** once per armed load, on the UI thread, when all of these hold:
  1. a frame of that load was presented to the session's surfaces (the tracker's latch counts a frame
     presented just before the load notice);
  2. this surface is attached to the session;
  3. the surface's presentation host is shown (the auto-hide rules; with `AutoHide` off, the adapter's
     own show rule) and the session presents (not Audio mode);
  4. composited and frame-sink paths: the surface's bitmap holds a frame published at or after that
     frame (a frame waiting in the mailbox is published first, in the same turn).
- **Where**, per path:
  - WPF composited: in the dispatcher operation that writes the frame into the `WriteableBitmap`
    (`PublishPendingFrame`, called inline when the first-frame notice arrives first), after the
    composited host is made `Visible`. Changes made in the handler render in the same WPF frame as
    that bitmap.
  - WPF native: after the swap-chain present returned and the child HWND is shown; DWM shows the frame
    at the next vblank, so WPF content changed in the handler can lead or lag it by a frame (best
    effort; native cannot sync WPF content: the overlay is a separate window).
  - Avalonia frame sink: in the UI turn that has the frame in the bitmap and invalidated the control,
    so the next Avalonia render draws both. Avalonia GPU shared image: in the UI turn after the
    present notice, with the composition visual shown (best effort, normally the same frame).
  - WinForms: frame sink, after the frame is published and the control shows frames; native window,
    after the child window is shown (best effort).
- **Not raised**: audio-only media, Audio mode, a load that closes or fails before its first frame,
  a surface that never attaches. A load whose first frame arrives while the surface is hidden by
  auto-hide (stopped) raises when the surface shows again.
- Threading: subscribe before calling `LoadAsync`; the handler runs on the UI thread and must not
  block. `PresentationFramePresented` (every frame, background thread, per session) is unchanged.

**UI-thread counter (047A's before numbers):** `MediaSurface.CompositedUiThreadTime` (`TimeSpan`) and
`MediaSurface.CompositedFramesPublished` (`long`) on the WPF surface: totals, since the surface was
created, of the time spent publishing composited frames on the UI thread (mailbox take, bitmap
(re)creation, `WritePixels`) and of the frames published. Read on the UI thread. The WPF test player's
stats line shows `UI thread x.x ms/s (y.yy ms/frame)` for each composited surface; `[Stats]` traces it.

## Acceptance (slice 1)

1. Core: `MediaSurfaceContentTracker` reports a per-load first-frame number (armed by a load with visual
   content or a session already open, set by the first present or the latch, cleared by close, error
   and session change), raising `Changed` when it is set, with `AutoHide` on or off. Unit tests.
2. WPF, Avalonia and WinForms `MediaSurface.FirstFrameReady` follow the proposal above; Avalonia
   headless tests (frame sink path) cover: once per load, a second load raises again, nothing for
   audio-only media, AutoHide off, a load before the surface is attached, nothing before the bitmap
   holds a frame.
3. WPF composited: raised in the bitmap-writing operation after the host is visible (manual smoke).
4. WPF `CompositedUiThreadTime`/`CompositedFramesPublished`; the WPF test player shows them in its
   stats line.
5. `StartupMediaDelay` removed from `PlayerViewModel`; its dispose test is replaced by one that the
   startup media loads without a delay.
6. Docs: the A/B swap pattern (and an inset clip) in the WPF adapter README; the update note with the
   manual smoke steps; the manifest.
7. Linux sandbox validation as in `HANDOFF.md`; boundary scripts and format pass.

## Out of scope

- Same-frame sync in native WPF mode (not possible: separate window), 047A's shared GPU image, image
  overlays (052A).
- A per-load id in the event args (Core has no load identity yet); one event per load is the contract.
