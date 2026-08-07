# Idea Stage 027A - UI Adapter Implementations

Status: Good idea.

## Why This Is Worth Doing

Additional UI adapters make the engine usable from WinUI 3, WinForms, and Avalonia
without duplicating playback or rendering logic. They also verify that the WPF adapter
boundary is genuinely framework-neutral rather than merely extracted in name.

## Recommended Adapter Order

1. `FatzBomb.MediaControl.WinUI` on Windows using existing Windows and Direct3D11 packages.
2. `FatzBomb.MediaControl.WinForms` on Windows as a small HWND lifecycle adapter.
3. `FatzBomb.MediaControl.Avalonia` on Windows to validate the cross-framework contract.
4. Extend Avalonia to Linux and macOS through the packages planned in
   `026A_IDEA_CrossPlatformRendererBackends.md` and
   `026B_IDEA_CrossPlatformPlatformServices.md`.

## Shared Adapter Contract

- Own framework control lifecycle, native surface creation, size, DPI, visibility, and focus.
- Pass `MediaPresentationSurfaceDescriptor` values to Core and release surfaces on detach.
- Expose framework-specific source conversions only where the framework requires them.
- Inherit or delegate the existing `IMediaControlSession` API rather than wrapping media logic.
- Marshal background-thread session events before touching UI state.

## Recommended Immediate Action

Extract a framework-adapter conformance checklist from WPF, then create a WinUI 3
`MediaSurface` vertical slice that attaches one primary surface, resizes, plays, stops,
and detaches using the existing Windows plus Direct3D11 composition bundle.

## Must Preserve

- Adapters own zero rendering, decode, composition, or media policy.
- Renderer selection remains a host composition-root decision.
- Secondary output uses the same Core session and final composition plan.
- Synchronous blocking APIs are not introduced into UI event paths.

## Explicit Non-Goals

- A shared base control that forces unrelated UI frameworks into one inheritance model.
- Reimplementing player controls, playlists, libraries, or application business logic.
- Referencing renderer internals, pipelines, CDG internals, or native GPU resources.
- Implementing every UI framework in one stage.
- Claiming Linux/macOS Avalonia support before matching platform and renderer packages exist.
