# FatzBombMedia

FatzBombMedia is a WPF media-player solution with a reusable media-control library and a lightweight WPF test player.

## Projects

| Project | Purpose |
|---|---|
| `FatzBombMedia.MediaControl` | Reusable WPF media-control library. Contains the media facade, BASS audio path, FFmpeg video decode path, CDG packet playback, rendering, metadata, archive support, normalization, pitch, tempo, and volume controls. |
| `FatzBombMedia.Player` | Lightweight WPF test shell that owns its display surface and attaches `FatzBombMedia.MediaControl` to it. It is intended for development testing and may later become a small standalone player. |

## Important native DLL note

The BASS and FFmpeg native DLLs are **not included**. The host app/test shell must provide them through configured native paths.

For local testing, put x64 native DLLs in `FatzBombMedia.Player/native/`. The host app copies that folder to output, sets `DLLPath.Bass` and `DLLPath.FFmpeg`, then creates `MediaPlayer`.

## Documentation map

Start here:

- `docs/00-START-HERE.md`
- `docs/01-Architecture-And-Usage.md`
- `docs/02-Features-And-Backlog.md`
- `docs/03-CDG-Protection.md`
- `docs/04-Audio-Video-Rendering.md`
- `docs/05-Native-Libraries-And-NuGet.md`
- `docs/06-Validation-And-Agent-Safety.md`

The strongest rule for future cleanup is simple:

> Do not remove CDG, BASS tempo/pitch/normalization, native loading, or video/audio sync code unless the regression checklist passes.

## Current implementation shape

The active media-control project now runs through the clean `Pipeline/` path for audio, CDG, video, archives, direct images, backgrounds, seek, and no-audio timing.

Current high-level decisions:

- Host apps own XAML, controls, display surfaces, and native DLL deployment.
- `MediaControl` owns playback, decoding, effects, timing, file/archive detection, and render output.
- Rendering selection is automatic.
- Video audio goes through BASS/BASS_FX, not direct FFmpeg audio output.
- The old packaged `MediaPlayerControl` and old concrete orchestration were removed from the active project.
- The `legacy/` folder is reference-only.

See `docs/02-Features-And-Backlog.md` for the remaining backlog.

## WPF D3DImage sizing guidance

For overlay-friendly WPF playback, the recommended default is a source-sized D3DImage surface. The WPF control lays the surface out normally so host apps can place WPF overlays above it without airspace issues. Avoid using display-sized D3DImage surfaces as a default; on large/ultrawide screens they can increase interop work and cause resize flicker.

Recommended defaults:

```csharp
WpfD3DImagePresentationMode = WpfD3DImagePresentationMode.IndependentPresentationLoop;
WpfD3DImageSurfaceSizingMode = WpfD3DImageSurfaceSizingMode.SourceNative;
```

For weaker laptops or 4K/oversized sources, use `SourceCapped` with `MaxWpfD3DImageSurfaceWidth` / `MaxWpfD3DImageSurfaceHeight` to cap the WPF interop surface without making it window-sized.

## Native visual compositor direction

The normal WPF player surface now targets `NativeHwndOverlay`: a native DirectX/DXGI child window for media pixels plus a transparent owned WPF overlay window for controls. D3DImage remains as a hidden fallback/diagnostic path, not the recommended product renderer.

The native compositor now presents still images, CDG frames, background images, and visual-only looping background video through the native D3D11 HWND surface where possible. CDG is alpha-blended over the current base layer before presentation.


## Latest patch: native background-video cadence rollback fix

See `NATIVE-BACKGROUND-VIDEO-CADENCE-ROLLBACK-FIX-NOTES.md`. This removes the experimental steady 60Hz CPU-composite presenter, restores frame-driven background-video presents, and increases background-video buffering/lookahead to reduce no-frame cadence jumps.

- `NATIVE-BACKGROUND-VIDEO-CDG-SCALED-OVERLAY-CACHE-NOTES.md` documents the CDG overlay scaling cache used to reduce background-video compositor jitter.

## Native DirectComposition Layer Patch

This build adds an experimental DirectComposition native layer path for CDG + background video. It keeps one HWND, uses a base composition swap-chain for video/image content, and uses a separate premultiplied-alpha CDG overlay swap-chain so CDG updates no longer force full-frame CPU background-video composition. See `NATIVE-DIRECTCOMPOSITION-LAYER-PATCH-NOTES.md`.


## Latest Patch

- `NATIVE-HWND-RESIZE-COMPILE-FIX-NOTES.md` fixes the missing `DispatcherPriority` import and suppresses analyzer-only Win32 interop messages from the resize patch.

- Native HWND resize freeze patch: freezes native presentation during manual WPF resize and applies one final native layout after resize settles. See `NATIVE-HWND-RESIZE-FREEZE-PATCH-NOTES.md`.
