# FatzBombMedia Renderer Refactor and CDG Layer Implementation Plan

## Purpose

This document defines the refactor path for the FatzBombMedia renderer and the implementation path for the CDG/background-layer presentation model after the refactor.

The goal is to stop patching CDG-specific behavior into the primary video renderer and instead create a clean, role-based rendering architecture where each layer has one responsibility.

The immediate bug being chased is CDG + background video/image flicker and aspect instability. The larger issue is that the renderer currently mixes too many responsibilities in one large file/path, causing small fixes to break unrelated behavior.

---

## Current Known State

After rollback of the last failed patch, the project should be back to a state where:

- Video displays again.
- CDG aspect was corrected to native aspect rather than forced 16:9.
- CDG + background video still has flicker/aspect instability.
- Layer 2 diagnostic may or may not still be disabled depending on the local branch state.
- The last patch that caused **no video** should be removed.

Before starting the refactor, verify the exact local state with Git:

```powershell
git status
git diff --stat
```

If diagnostic patches are still present, decide whether to keep them temporarily or roll them back before the architectural refactor. Prefer starting from the latest buildable state where video displays.

---

## Confirmed Observations

### 1. CDG native aspect is required

CDG should use its native frame aspect, typically:

```text
300 x 216
```

The old option to draw CDG as a 16:9/widescreen layer should remain removed. CDG lyrics should scale **Uniform** into the available viewport.

### 2. Exact 16:9 control size removes flicker

When the control is set to exactly `1280x720`, flicker disappears.

This strongly indicates an aspect-rectangle disagreement. At 16:9, two different render paths produce the same visible rectangle, so the bug is hidden. At any non-16:9 size, the paths diverge and flicker becomes visible.

Likely issue:

```text
Path A uses full viewport / background-layer geometry.
Path B uses normal video output rect or CDG overlay rect.
The renderer alternates between them during updates.
```

### 3. Layer 2 is probably not the primary cause

Disabling the current CDG background wash / tint layer did not eliminate the major flicker.

Layer 2 still needs to be redesigned, but it is probably not the main cause of the current flashing.

### 4. CDG rendering activity is influencing background video behavior

When CDG lyrics are visible/updating, background video can temporarily zoom-to-fill.

When CDG lyric rendering is disabled, background video no longer zooms-to-fill even though the media session is still a CDG session.

This means the background media stretch behavior is currently tied to **CDG rendering activity**, not stable **media/session state**.

That is the wrong architecture.

### 5. The current renderer is too entangled

The main DirectX renderer currently handles too many responsibilities:

- D3D device creation
- swap chain / DirectComposition target
- resize behavior
- primary video upload/presentation
- background video/image presentation
- CDG overlay upload
- CDG background tint/wash
- aspect/stretch calculations
- fallback rendering
- native/WPF presentation concerns

This causes fixes in one area to break unrelated areas.

---

## Desired Layer Model

For CDG or any future overlay session, rendering should be role-based.

### Layer 1: Background media

Used when an image or video is acting as a background behind an overlay.

Rules:

```text
Target: full visible viewport/control
Stretch: UniformToFill
Audio: disabled/silent
Loop: true for background video
Update cadence: independent from CDG/overlay updates
```

This layer should not know about:

```text
CDG frame invalidation
CDG dirty state
CDG lyric rect
CDG packet timing
```

It should only know:

```text
I am a background layer.
I fill the viewport.
I use UniformToFill.
```

### Layer 2: Overlay background wash

For CDG, this is the full-screen color wash derived from CDG background color and user transparency.

Rules:

```text
Target: full visible viewport/control
Color: current CDG background color
Opacity: user CDG background transparency setting
Update cadence: only when color or transparency changes
```

It must not be aspect-bound to the CDG lyrics rectangle.

### Layer 3: Foreground overlay graphics

For CDG, this is the lyric/graphics texture.

Rules:

```text
Source: native CDG frame, typically 300x216
Background pixels: transparent
Target: Uniform-fit rectangle inside the full viewport
Update cadence: only when CDG pixels change
```

CDG lyrics should not draw the background wash. The wash is Layer 2.

---

## Main Architectural Decision

Do not continue adding CDG-specific `if` statements inside the normal video renderer.

Instead, split media into explicit roles:

```text
Primary media
Background media
Foreground overlay
```

Example sessions:

```text
Normal video:
  Primary = video
  Background = none
  Overlay = none

MP3 + CDG:
  Primary = audio
  Background = optional image/video
  Overlay = CDG

CDG + companion video:
  Primary = audio/CDG session
  Background = companion video
  Overlay = CDG

Future overlay type:
  Primary = audio/video
  Background = optional image/video
  Overlay = overlay renderer
```

Renderer behavior should be selected by role, not by scattered extension/type checks.

---

## Target Renderer Organization

The current large renderer should be split into smaller reusable parts.

### 1. `ViewportGeometry`

Pure geometry/aspect helper. No DirectX, no media state.

Responsibilities:

```text
CalculateViewportRect
CalculateUniformFitRect
CalculateUniformToFillSourceRect
CalculateUniformToFillDestRect
CalculateStretchRect
CalculateCdgLyricsRect
```

This is where aspect math should live. It must be deterministic and unit-testable.

Recommended location:

```text
FatzBombMedia.MediaControl/Core/Graphics/Geometry/ViewportGeometry.cs
```

Possible API shape:

```csharp
public static class ViewportGeometry
{
    public static RectD FullViewport(int width, int height);

    public static RectD UniformFit(
        double sourceWidth,
        double sourceHeight,
        double targetWidth,
        double targetHeight);

    public static RectD UniformToFillDest(
        double sourceWidth,
        double sourceHeight,
        double targetWidth,
        double targetHeight);

    public static RectD UniformToFillSourceCrop(
        double sourceWidth,
        double sourceHeight,
        double targetWidth,
        double targetHeight);
}
```

### 2. `D3DDeviceResources`

Owns reusable D3D resources.

Responsibilities:

```text
D3D11 device
DXGI device/factory
device context
samplers
blend states
shaders
device lost/recovery
```

It should not understand CDG, video, image, or stretch rules.

Recommended location:

```text
FatzBombMedia.MediaControl/Core/Graphics/D3D/D3DDeviceResources.cs
```

### 3. `NativePresentationTarget`

Owns the presentation surface.

Responsibilities:

```text
swap chain or DirectComposition target
viewport size
resize begin/end
present
clear/backbuffer ownership
```

It should not decide whether something is CDG or video.

Recommended location:

```text
FatzBombMedia.MediaControl/Core/Graphics/D3D/NativePresentationTarget.cs
```

### 4. `TextureUploader`

Uploads CPU/source frames to GPU textures.

Responsibilities:

```text
video frame -> GPU texture
image bitmap -> GPU texture
CDG ARGB frame -> GPU texture
solid color/wash texture if needed
```

It should not pick destination rectangles.

Recommended location:

```text
FatzBombMedia.MediaControl/Core/Graphics/D3D/TextureUploader.cs
```

### 5. `VideoLayerRenderer`

Reusable low-level video/image drawing helper.

Responsibilities:

```text
Draw textured quad
Apply source crop
Apply destination rect
Use premultiplied/opaque blend mode as needed
```

This should be reusable by both primary video and background video.

Recommended location:

```text
FatzBombMedia.MediaControl/Core/Graphics/Layers/VideoLayerRenderer.cs
```

### 6. `PrimaryVideoRenderer`

Policy wrapper for normal video playback.

Rules:

```text
Respects user Stretch setting
Uses audio-capable primary video pipeline
No forced loop unless transport says so
No CDG knowledge
```

Recommended location:

```text
FatzBombMedia.MediaControl/Core/Graphics/Layers/PrimaryVideoRenderer.cs
```

### 7. `BackgroundMediaRenderer`

Policy wrapper for background image/video playback.

Rules:

```text
Always full viewport
Always UniformToFill
Background video is silent
Background video loops
Independent from overlay/CDG dirty state
```

Recommended location:

```text
FatzBombMedia.MediaControl/Core/Graphics/Layers/BackgroundMediaRenderer.cs
```

This is the key piece for the current bug.

### 8. `CdgWashRenderer`

Draws Layer 2 only.

Responsibilities:

```text
Full viewport color/opacity wash
Cache current ARGB/opacity
Update only when color/transparency changes
```

It must not draw CDG lyrics.

Recommended location:

```text
FatzBombMedia.MediaControl/Core/Graphics/Layers/CdgWashRenderer.cs
```

### 9. `CdgLyricsRenderer`

Draws Layer 3 only.

Responsibilities:

```text
Upload 300x216 transparent CDG lyric texture
Uniform-fit to viewport
Update only when CDG frame changes
```

It must not draw Layer 2.

Recommended location:

```text
FatzBombMedia.MediaControl/Core/Graphics/Layers/CdgLyricsRenderer.cs
```

### 10. `LayerCompositor`

Owns draw order.

Responsibilities:

```text
Begin scene
Draw background media
Draw overlay wash
Draw overlay graphics
Present complete frame
```

It is the only place that should know the layer order.

Recommended location:

```text
FatzBombMedia.MediaControl/Core/Graphics/Layers/LayerCompositor.cs
```

---

## Refactor Path

The refactor should be done in small buildable stages. Do not rewrite the renderer all at once.

Each stage should:

1. Build.
2. Preserve current behavior unless explicitly changed.
3. Include a simple test checklist.
4. Avoid unrelated cleanup.

---

### Stage 0: Establish baseline

Goal: confirm a buildable starting point.

Tasks:

- Ensure the last no-video patch is rolled back.
- Build the solution.
- Confirm normal video still plays.
- Confirm CDG still displays.
- Confirm CDG + background video still reproduces the flicker.

Do not attempt to fix the flicker in this stage.

Acceptance:

```text
Normal video displays.
Audio works.
CDG displays.
CDG + background video reproduces known flicker/aspect bug.
```

---

### Stage 1: Extract viewport/aspect geometry

Goal: remove scattered aspect math from renderer methods.

Tasks:

- Create `ViewportGeometry` and any needed small rect struct/helper.
- Move existing Uniform, UniformToFill, and CDG overlay rect calculations into that helper.
- Replace duplicated aspect calculations with calls to the helper.
- Do not change behavior yet.

Important:

This stage is refactor-only. If output changes, back up and find why.

Acceptance:

```text
Build succeeds.
Normal video behavior unchanged.
CDG-only behavior unchanged.
CDG + background video bug still reproduces exactly as before.
```

Why this first:

The confirmed bug is aspect related. Before changing behavior, aspect math must be centralized so later fixes are not scattered across several paths.

---

### Stage 2: Introduce explicit media/layer roles

Goal: classify media by role before it reaches the renderer.

Tasks:

- Add role/state model above the D3D renderer.
- Suggested concepts:

```csharp
public enum MediaLayerRole
{
    None,
    Primary,
    Background,
    ForegroundOverlay
}

public enum BackgroundMediaKind
{
    None,
    Image,
    Video
}

public enum ForegroundOverlayKind
{
    None,
    Cdg
}
```

- Determine these values during media open/session construction, not during CDG frame updates.
- Expose stable state to the renderer:

```text
HasOverlaySession
HasBackgroundMedia
BackgroundMediaKind
ForegroundOverlayKind
```

Important:

Do not use CDG dirty/invalidated/render activity to decide whether background media should use background behavior.

Acceptance:

```text
Build succeeds.
Debug/log output or debugger confirms:
  CDG + companion video = BackgroundMediaKind.Video + ForegroundOverlayKind.Cdg
  Normal video = Primary video only
```

---

### Stage 3: Create `BackgroundMediaRenderer` path

Goal: route companion/manual background video/image through a dedicated background path.

Tasks:

- Create `BackgroundMediaRenderer`.
- It should use shared upload/draw helpers where practical.
- For video background:

```text
silent
looped
full viewport
automatic UniformToFill
```

- For image background:

```text
full viewport
automatic UniformToFill
```

- Do not route background video through the primary video stretch/output-rect path.
- Do not let CDG invalidation call into background stretch logic.

Acceptance:

Test with CDG lyric rendering disabled:

```text
CDG session + background video displays.
Background video zooms-to-fill.
No CDG lyrics are required for zoom-to-fill to happen.
```

Test normal video:

```text
Normal video still respects user Stretch setting.
Normal video audio still works.
Normal video is not forced to loop.
```

This is the main architectural fix for the current Layer 1 problem.

---

### Stage 4: Re-enable CDG lyrics over background media

Goal: make CDG updates only affect the CDG lyric layer.

Tasks:

- Create/use `CdgLyricsRenderer`.
- Ensure it owns only the 300x216 transparent lyric texture.
- CDG frame invalidation should do only:

```text
update CDG texture
request composite/present
```

- CDG frame invalidation must not:

```text
change background stretch
change background destination rect
resize swap chain
rebind background content
clear/present partial frame
```

Acceptance:

```text
CDG + background video displays.
Background video remains UniformToFill even when CDG is idle.
Background video remains UniformToFill when CDG updates rapidly.
CDG lyrics display Uniform/native aspect over viewport.
No 16:9-only flicker behavior.
```

---

### Stage 5: Rebuild Layer 2 as `CdgWashRenderer`

Goal: implement CDG background color wash as its own full-viewport layer.

Tasks:

- Create `CdgWashRenderer`.
- It should cache:

```text
current CDG background color
current CDG background transparency/opacity
```

- It should update only when one of those values changes.
- It should draw over full viewport.
- It should not be tied to the CDG lyric rect.
- It should not be drawn as part of the CDG lyric texture.

Acceptance:

```text
Changing CDG background transparency updates wash opacity.
Changing CDG background color updates wash color.
CDG wash covers full control/window.
No black border outside CDG lyric area.
No flicker caused by wash-only changes.
```

---

### Stage 6: Shrink `VorticeD3D11Renderer`

Goal: turn the large renderer into a coordinator instead of a giant implementation file.

Tasks:

- Move low-level device/presentation logic into resource classes.
- Move layer drawing into layer renderer classes.
- Keep public renderer API stable where possible.
- Remove dead diagnostic flags and stale CDG path checks.

Target shape:

```csharp
public sealed class VorticeD3D11Renderer
{
    private readonly D3DDeviceResources _deviceResources;
    private readonly NativePresentationTarget _presentationTarget;
    private readonly LayerCompositor _compositor;
    private readonly PrimaryVideoRenderer _primaryVideo;
    private readonly BackgroundMediaRenderer _backgroundMedia;
    private readonly CdgWashRenderer _cdgWash;
    private readonly CdgLyricsRenderer _cdgLyrics;
}
```

Acceptance:

```text
Renderer file is significantly smaller.
Layer-specific behavior is no longer mixed into the primary video path.
Normal video, CDG, CDG + image, and CDG + video all still work.
```

---

## Implementation Path After Refactor

After the renderer is split into stable roles/layers, implement the final intended behavior.

### Implementation 1: Official background media behavior

Rules:

```text
When media role = Background:
  video is silent
  video loops
  video uses UniformToFill
  image uses UniformToFill
  target is full viewport
```

This should be used for:

```text
CDG + companion video
CDG + companion image
MP3+CDG + companion video/image
manual background media behind overlay
future overlay sessions
```

Normal video-only playback must not use this path.

---

### Implementation 2: Transparent CDG lyric texture

CDG lyrics should be drawn over Layer 2.

Rules:

```text
CDG background color pixels become transparent in the lyric texture.
CDG non-background/lyric pixels remain visible.
Texture remains native CDG size, typically 300x216.
Texture draws Uniform to viewport.
```

This preserves native CDG aspect while avoiding black/colored borders around the CDG rect.

---

### Implementation 3: Full-viewport CDG wash

Layer 2 should provide the old background-opacity effect without being locked to CDG aspect.

Rules:

```text
Full viewport rectangle/color clear
Color = CDG current background color
Opacity = user CDG background transparency setting
Cached and updated only on color/transparency change
```

This replaces the old behavior where CDG background opacity was baked into the CDG frame itself.

---

### Implementation 4: Optional DirectComposition multi-visual architecture

Only consider this after the role/layer refactor is stable.

Possible future architecture:

```text
Native HWND / DirectComposition target
  Root visual
    Background media visual
    CDG wash visual
    CDG lyrics visual
    Optional WPF/native UI overlay visual/window
```

Do not start with multiple HWND swap chains.

If using multiple swap chains/surfaces, prefer DirectComposition visuals rather than overlapping HWNDs. The goal would be independent layer update cadence, not scattered swap chains.

Potential benefits:

```text
Video updates at video cadence.
CDG lyrics update only when CDG changes.
Wash updates only when color/opacity changes.
DWM/DirectComposition handles composition efficiently.
```

Risks:

```text
More complex resize handling.
More complex device-lost handling.
Alpha/premultiplied-alpha issues.
Present ordering issues.
Harder debugging.
```

This is a later optimization, not the first fix.

---

## Guardrails

### Do not use CDG render activity as a session flag

Wrong:

```text
if CDG frame dirty/current overlay texture exists -> background uses UniformToFill
```

Right:

```text
if media role is Background in an overlay session -> background uses UniformToFill
```

### Do not let CDG update resize background media

CDG invalidation must never change:

```text
background media destination rect
background stretch mode
native presentation size
swap chain size
```

### Do not let normal video inherit background behavior

Normal video-only playback must still:

```text
respect user Stretch setting
use audio
not force loop
not use CDG/background media path
```

### Do not draw Layer 2 inside Layer 3

CDG wash and CDG lyrics must remain separate.

Wrong:

```text
CDG texture contains semi-transparent background + lyrics
```

Right:

```text
Layer 2 = full viewport wash
Layer 3 = transparent CDG lyrics texture
```

### Do not present partial frames

Compositor should present only after complete layer composition.

Wrong:

```text
clear -> present
background -> present
CDG -> present
```

Right:

```text
clear/background/wash/lyrics -> present once
```

---

## Test Matrix

Run this matrix after each major stage.

### Normal video-only

Expected:

```text
Video displays.
Audio plays.
Stretch setting is respected.
No forced loop.
No CDG/background behavior.
Resize remains smooth.
```

### Audio-only

Expected:

```text
Audio plays.
No video surface flicker.
No background renderer accidentally active.
```

### Image-only

Expected:

```text
Image displays according to image/main presentation rules.
No CDG overlay behavior unless overlay session exists.
```

### CDG-only

Expected:

```text
CDG lyrics display native aspect, Uniform to viewport.
No forced 16:9 stretch.
No severe resize slowdown.
```

### MP3 + CDG

Expected:

```text
Audio plays.
CDG lyrics display native aspect.
No background media unless configured/found.
```

### CDG + background image

Expected:

```text
Background image fills viewport UniformToFill.
CDG wash covers full viewport when enabled.
CDG lyrics display Uniform over it.
No black border around CDG area.
```

### CDG + background video

Expected:

```text
Background video is silent.
Background video loops.
Background video fills viewport UniformToFill.
CDG lyrics display Uniform over it.
CDG updates do not change background size.
No 16:9-only flicker behavior.
Resize remains usable.
```

### Resize test

Run these sizes:

```text
1280x720 exact 16:9
1200x720 non-16:9
1000x720 non-16:9
1280x800 non-16:9
full screen
manual drag resize
```

Expected:

```text
No geometry toggling between sizes.
No flicker that only disappears at exact 16:9.
No severe slowdown during manual resize.
```

---

## Suggested Commit Sequence

Use small commits so each change can be reverted independently.

```text
1. Renderer baseline cleanup / remove failed diagnostics
2. Extract ViewportGeometry
3. Add media layer role model
4. Add BackgroundMediaRenderer shell
5. Route background image through BackgroundMediaRenderer
6. Route background video through BackgroundMediaRenderer silent/loop/UniformToFill
7. Add CdgLyricsRenderer shell and route CDG texture through it
8. Add CdgWashRenderer shell and re-enable Layer 2
9. Move D3D resource/presentation code into smaller classes
10. Remove dead/stale renderer branches
```

Each commit should build.

---

## Agent Instructions

When giving this plan to another coding agent:

```text
Do one stage only.
Do not combine stages.
Do not rewrite the whole renderer at once.
Do not change public behavior unless the stage explicitly says so.
Do not add more CDG-specific checks to the primary video path.
Preserve normal video playback.
Preserve CDG native aspect.
Keep changes buildable.
Report exact files changed.
```

Good first agent task:

```text
Extract the scattered viewport/aspect rectangle calculations from VorticeD3D11Renderer into a new ViewportGeometry helper. Preserve current behavior exactly. Do not fix the flicker yet. Build after extraction.
```

Good second agent task:

```text
Introduce explicit media layer role state above the renderer: primary media, background media, foreground overlay. Classify CDG + companion video/image as background media + CDG overlay. Do not alter rendering behavior yet except to expose the role state cleanly.
```

Good third agent task:

```text
Create a BackgroundMediaRenderer that renders image/video backgrounds to the full viewport using UniformToFill. Background video must be silent and looped. Route only CDG/overlay-session companion background media through it. Do not affect normal video-only playback.
```

---

## Final Design Summary

The final renderer should not ask:

```text
Is CDG drawing right now?
Did CDG invalidate this frame?
Does a CDG overlay texture currently exist?
```

To decide how background video is drawn.

It should ask:

```text
What role is this media layer?
```

Then apply fixed role behavior:

```text
Primary video:
  user stretch, audio, normal playback

Background video:
  silent, loop, UniformToFill, full viewport

CDG wash:
  full viewport color/opacity

CDG lyrics:
  native 300x216 transparent texture, Uniform to viewport
```

This should remove the current flicker/aspect dependency and make future overlay features much easier to implement.
