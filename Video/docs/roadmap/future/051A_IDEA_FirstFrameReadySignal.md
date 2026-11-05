# Stage 051A (planned, next) - First-frame-ready signal and the A/B swap pattern

Owner decision 2026-11-05 (TriviaBomb, a WPF host replacing LibVLC). Queue item 1.

- A per-surface event on every adapter (WPF, Avalonia, WinForms), raised on the UI thread when the
  first frame of newly loaded media is shown on that surface. In WPF composited mode it must be
  raised in the same dispatcher turn as the bitmap write (`WpfCompositedSurfaceHost.PublishPendingFrame`),
  after the host is made visible, so a host change made in the handler renders in the same WPF frame.
  Native mode: raised when the swap chain shows it (best effort; native cannot sync WPF content).
- Builds on `MediaSurfaceContentTracker` (load arms, first presented frame shows). Check: a hidden
  surface (the B player of the pair) must still be presented to, or the pattern documents
  `Opacity = 0` instead of `Collapsed`.
- Docs: the A/B swap pattern (two `Background`-mode players; load into the hidden one; on the event,
  in one UI-thread turn show it, hide the old one, change the screen). The same for an inset clip.
- `PresentationFramePresented` (every frame, background thread, not per load) stays as is.
- Tests: tracker/adapter logic through the existing doubles; WPF same-frame behaviour is a manual
  smoke on Windows.
