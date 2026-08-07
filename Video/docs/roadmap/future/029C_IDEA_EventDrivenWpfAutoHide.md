# Idea Stage 029C - Event-Driven Surface AutoHide (WPF)

Status: Good idea. Recommended after Stage 029A.

## Why This Is Worth Doing

The lightweight WPF surfaces added in Stage 028A need a consistent visibility policy:
shown only when there is compatible visual content, hidden on stop, close, failure, or
audio-only media. Without a deliberate AutoHide model, every host hand-rolls visibility
logic per surface and gets the edge cases (images vs. explicit stop) subtly wrong.

## Recommended Direction

- Applies to `MediaSurface`, `BackgroundSurface`, and `KaraokeSurface`.
- Show when compatible visual content is loaded and presented (loaded image, video, or
  a CDG/subtitle-capable media that actually presents visuals).
- Hide on explicit `Stop`, explicit `Close`, load failure, and audio-only media.
- Expose an `AutoHide` switch (opt-in) plus a read-only visibility state property and a
  visibility-changed event so hosts can observe or override.
- Visibility is decided from Core session events and state (Stage 029A surface), never
  inferred from file extension — no extension-based guessing.
- Record the image/stopped-state semantic explicitly: an explicit `Stop` hides the
  surface, while a loaded static image (loaded but not playing) shows it. These are
  different states and must not collapse into one visibility rule.
- Preserve the WPF adapter boundary: the adapter translates Core signals into UI
  visibility only; no media, renderer, pipeline, or composition logic enters the WPF
  project.

## Recommended Immediate Action

Map each Core session signal (loaded, started, stopped, closed, load-failed,
audio-only) to a visibility intent for each of the three surfaces, and record the
image vs. explicit-stop distinction in a small state table before wiring events.

## Must Preserve

- Manual visibility control when AutoHide is off.
- Surface attach/reattach and secondary-window behavior from Stage 028A.
- The WPF boundary checks (no new `using` that pulls Core renderer, pipeline, internal
  Cdg, or D3D11 namespaces into the adapter).
- Existing `MediaSurface` native-HWND and composited presentation modes.

## Explicit Non-Goals

- Extension-based or content-sniffing visibility decisions.
- Moving visibility policy into Core; Core supplies signals, adapters apply them.
- Changing Stage 028A surface creation, sizing, or presentation semantics.
- New renderer or pipeline behavior in this stage.
