# FatzBombMedia Renderer Smoke Test Checklist

_Last updated after Stage 7G._

Use this checklist after renderer refactors or backend changes. The goal is to catch regressions in presentation backend selection, CDG/background layering, live resize, and fallback paths.

## Build and startup

- [ ] Solution builds cleanly.
- [ ] Player app starts without native DLL path errors.
- [ ] No renderer initialization exceptions appear in output/debug logs.
- [ ] Presentation backend logs match the selected player mode.

## Basic media

- [ ] Audio-only file plays.
- [ ] Volume changes work.
- [ ] Pitch changes work.
- [ ] Tempo changes work.
- [ ] Normalization toggle does not crash and audio still plays.
- [ ] Seek works on audio-only playback.

## Video

- [ ] Normal video displays.
- [ ] Video keeps the expected aspect/stretch mode.
- [ ] Video seek works.
- [ ] Tempo with video keeps BASS-processed audio and video reasonably synced.
- [ ] Resize during video playback does not crash.
- [ ] Repeated rapid resize during video playback does not crash.
- [ ] Fullscreen enter/exit works during video playback.

## Images/static media

- [ ] Direct image file renders as a still surface.
- [ ] Image remains visible after resize.
- [ ] Image stretch/quality settings still behave as expected.

## CDG-only / karaoke

- [ ] MP3+CDG renders lyrics.
- [ ] ZIP audio+CDG renders lyrics.
- [ ] CDG lyrics keep native CDG aspect and do not stretch to 16:9.
- [ ] CDG transparent/background handling looks correct.
- [ ] Seek during CDG playback updates lyrics correctly.
- [ ] Resize during CDG playback does not crash.
- [ ] Fullscreen enter/exit during CDG playback does not crash.

## CDG with background image/video

- [ ] Same-name sidecar image appears behind CDG.
- [ ] Internal ZIP image appears behind CDG.
- [ ] Same-name sidecar background video appears behind CDG.
- [ ] Internal ZIP background video appears behind CDG.
- [ ] Background image/video fills the full viewport using the CDG background-layer rule.
- [ ] CDG lyrics remain aspect-fit over the background.
- [ ] Background video remains silent/visual-only.
- [ ] Background video loops independently from audio/CDG.
- [ ] CDG lyric updates do not cause background image/video to alternate between different aspect rectangles.
- [ ] Resize during CDG + background video does not crash.
- [ ] Rapid resize during CDG + background video does not crash.

## Presentation backend paths

### D3DImage

- [ ] D3DImage mode displays video.
- [ ] D3DImage mode displays CDG.
- [ ] D3DImage mode displays image/static content.
- [ ] D3DImage mode resizes without crash.

### Native HWND

- [ ] Native HWND mode displays video.
- [ ] Native HWND mode displays CDG.
- [ ] Native HWND mode displays CDG + background image/video.
- [ ] Native HWND mode resizes without crash.
- [ ] Native HWND live resize does not produce `ExecutionEngineException` or null-target D3D errors.

### Native HWND overlay / DirectComposition-style overlay path

- [ ] Overlay mode creates expected native/overlay windows.
- [ ] CDG overlay appears above the base/background surface.
- [ ] Overlay remains aligned after resize.
- [ ] Overlay remains aligned after fullscreen enter/exit.
- [ ] Overlay mode does not steal mouse input when configured as passive/click-through.

## Fallback paths

- [ ] CPU `WriteableBitmap` fallback can render BGRA frames.
- [ ] CPU `WriteableBitmap` fallback can render CDG ARGB frames.
- [ ] Cleanup/reinitialize does not leave stale frozen/invalid bitmap surfaces.

## Cleanup / lifecycle

- [ ] Load one file, stop, load another file.
- [ ] Switch from video to CDG.
- [ ] Switch from CDG to image.
- [ ] Switch from native HWND mode to overlay mode if supported by the test UI.
- [ ] Close app while media is playing.
- [ ] Close app during resize.
- [ ] Close app during CDG + background video playback.

## Debug output to watch

Investigate if any of these appear:

```text
ExecutionEngineException
UpdateSubresource failed
presentationTexture null
invalid presentation target
native resize failed
DirectComposition overlay upload failed
D3D device removed
```

A skipped frame during resize is acceptable if logged cleanly. A crash or unmanaged exception is not.

