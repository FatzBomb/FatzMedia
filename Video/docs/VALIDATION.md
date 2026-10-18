# Validation Checklist

Updated: 2026-08-22

This checklist is for validating `FatzBombMedia` after cleanup, renderer, timing, WPF adapter, CDG, audio/clock, or reliability changes.

Use the **Routine Validation** sections after normal code changes. Use the **Extended Validation** sections after renderer/presentation/timing/threading/state changes or before merging a major roadmap stage.

## Latest Validated Baseline

On 2026-07-18, the host developer completed the Player visual smoke checklist after
the Core presentation/renderer boundary migration and reported no visible regressions.
The retained-frame refresh after Stop was also verified: subsequent Play restores video
immediately without requiring a seek.
The package-installed native smoke also passed for caller-supplied paths and the
predefined `runtimes/win-x64/native` layout. Forced GPU device-loss recovery was not
part of this visual sign-off and remains an extended diagnostic scenario.

On 2026-08-03, Stage 025B validation passed with 328 automated tests and successful
manual smoke coverage for stop-to-play restoration, image/video sidecar policy, and
tracker-format seeking and looping across the affected playback states.

On 2026-08-06, the Stage 028A reliability follow-up passed 348 automated tests, Release
build, formatting, architecture gates, package creation, and package-installed native
loading for caller-supplied and predefined paths. Visual verification of both WPF
presentation modes and forced managed-sink device loss remains manual.

On 2026-08-07, Stage 028A was closed with Release build zero warnings, `dotnet format`
verification, Core/WPF/presentation-parity gates, and 348 passing tests. Manual checks
confirmed video seeking resumes near its settled frame without skipping ahead, and a
fresh rebuild of `D:\- Testing Files\mario.mod` returns the seekbar to about 4 seconds
at its authored loop. WPF composited visual scenarios (clipping, transforms, opacity,
interactive overlays, background/karaoke surface coverage in both modes) and forced
device loss remain outstanding extended/manual validation; they are tracked here and
are not Stage 028A blockers.

On 2026-08-07, Stage 024A was completed. Automated validation passed: Debug and Release
builds with zero warnings, 370 automated tests (including the new software-renderer
exact-pixel and backend suites), `dotnet format` verification, Core/WPF/presentation-parity
gates, and `FatzBomb.MediaControl.Rendering.Software` nupkg and snupkg pack. The final
manual Direct3D11 Player regression smoke passed on 2026-08-07: the Release
`NativeHwndOverlay` Player was verified for normal video/CDG play, seek, stop, and replay
with no observed regressions, closing the stage.

On 2026-08-08, Stage 029A was closed. Automated validation passed: Release build with 0
warnings/errors, 389 tests, `dotnet format` verification, Core/WPF/presentation-parity
gates, and Core package creation. Manual Player host smoke passed for the new session
events across play, pause, stop, seek, natural end, and loop, plus
volume/pitch/tempo/normalize/loop toggles, with no `PositionChanged` while paused and no
control-queue stalls. `WpfComposited` running at a lower frame rate than
`NativeHwndOverlay` is expected because composited frames go through managed GPU
readback. The smoke exposed and the stage fixed an overlay visibility bug in
`WpfComposited` and added the `--software` Player-host option (software renderer,
software decoding, pinned `WpfComposited`). The software-renderer runtime smoke via
`--software` passed on 2026-08-08, closing the final 029A manual item.

On 2026-08-08, Stage 029B was closed. Automated validation passed: Release build with 0
warnings/errors, 397 tests, `dotnet format` verification, Core/WPF/presentation-parity
gates, and Core package creation, covering the per-load pitch/tempo reset options,
`MediaChangeReason`, and the preserved `MediaValueChangedEventArgs` constructor
compatibility. A strict implementation review reported no blockers. The manual Player
reset smoke passed the same day with both startup flags
(`--reset-pitch-on-load` / `--reset-tempo-on-load`): Core pitch reset to `0` and tempo
reset to `100` on load, with both WPF sliders visibly synchronized to their neutral
positions; default behavior (flags off) preserved user-set values across loads.

Stage 029C (event-driven WPF surface AutoHide) was closed on 2026-08-08. Automated
validation passed: Release build with 0 warnings/errors, 406 tests, `dotnet format`
verification, Core/WPF/presentation-parity gates, Core and WPF nupkg/snupkg packs,
and WPF specialist plus final diff reviews with no blockers. The manual Player
AutoHide smoke passed the same day, confirming visual↔audio-only transitions,
video/CDG replacement (never stuck hidden), default AutoHide-on behavior with the
`--no-auto-hide` opt-out, background/transparency semantics (only the internal
native/composited host is hidden/shown; the outer control is untouched), and the
public `IsContentVisible` / `ContentVisibilityChanged` behavior. Stage 030A
(base-visual transform pipeline) is the sole active roadmap stage: implemented
2026-08-09 with automated validation green (see below); its manual visual-
transform smoke on both renderer paths is pending and is required before the
stage can be closed.

On 2026-08-09, Stage 030A was implemented. Automated validation passed:
Release build with 0 warnings/errors, 429 tests, `dotnet format` verification,
and Core/WPF/presentation-parity gates all green. Core, Software, and Direct3D11
nupkg + snupkg creation passed. The manual visual-transform smoke on both
renderer paths (Direct3D11 `NativeHwndOverlay` and `--software` `WpfComposited`)
is pending; the checklist is in the Stage 030A section below.

On 2026-08-10, Stage 030A added the Player advanced validation controls: a
collapsed `ADVANCED VALIDATION` expander with a live Auto Hide checkbox, runtime
Reset Pitch on Load / Reset Tempo on Load checkboxes (additive
`IMediaControlSession` properties via `MediaControlPlayer.LoadBehavior.cs` and
mutable `MediaFile` options), RED GAIN / GREEN GAIN / BLUE GAIN (0–2), HUE,
BRIGHTNESS (−0.25–0.25), CONTRAST (0.5–1.5), and SATURATION (0–2) sliders (no
Lightness), an IDENTITY/ACTIVE indicator, and a Reset Visual command. The
controls work independently without startup flags; the existing startup flags
initialize their corresponding controls for compatibility. The same day the
contract was revised: independent per-channel RGB gains (`RedGain`/`GreenGain`/
`BlueGain`, [0,2] default 1) replace the former tint-then-color-wash members and
the HSL `Lightness` member was removed from the model and math (the former
`ColorWashPreset.cs` type/file was dropped — no preset type remains). Automated
validation passed: Release build with 0 warnings/errors, 430 tests, `dotnet
format` verification, and Core/WPF/presentation-parity gates all green. Core,
Software, and Direct3D11 nupkg + snupkg creation passed. The manual
visual-transform/UI smoke on both renderer paths (Direct3D11
`NativeHwndOverlay` and `--software` `WpfComposited`) remains the only pending
gate; the checklist is in the Stage 030A section below.

On 2026-08-11, the Stage 030A contract was finalized. The fixed operation order
changed to hue → saturation → **TV-style contrast gain** (`rgb *= Contrast`,
black remains black) → **brightness offset** (`rgb += Brightness`) → per-channel
RGB gains, and a new `IncludeOverlays` member (default `false`) adds a
whole-composition mode: with `IncludeOverlays = true` and a non-identity
transform, the same math is applied to the completed composition as a whole
(wash, CDG, subtitle, and other overlays) through a final postprocess that moves
— never double-applies — the transform off Layer 0. Software postprocesses the
premultiplied final buffer; Direct3D11 composites into a per-target cached
offscreen B8G8R8A8 RTV and runs `PSBaseVisualPremultiplied` as an overwrite
pass, with the DirectComposition overlay split disabled in this mode. The Player
host UI was reworked to a persistent bottom chrome (timeline/transport/
fullscreen/volume/status) plus one-at-a-time Sync/Video/Audio/A-V flyout
buttons; flyout content moved to `Controls/PlayerFlyoutPanel.xaml`; the MIDI
lyrics checkbox was removed and `ShowMidiLyrics = true` is always set. Latest
independent validation passed: Release build with 0 warnings/errors, 437 tests,
`dotnet format` verification, and Core/WPF/presentation-parity gates all green;
Core, Software, and Direct3D11 nupkg + snupkg creation passed; final
implementation reviews reported no blockers after fixes. The manual D3D11 +
`--software` flyout/final-composite visual/UI smoke remains the only pending
gate; the checklist is in the Stage 030A section below.

On 2026-08-11, Stage 030A fixes landed. **Root cause recorded**: with
`IncludeOverlays = false`, the DirectComposition split-path cached base state
for static images/backgrounds omitted `BaseVisualTransform`, so transform
changes were skipped for static images while per-frame video revisions masked
the bug. **Fix**: `DirectCompositionOverlaySplitBaseState` now includes
`BaseVisualTransform`, so any transform change invalidates the cached split
base state and forces a re-present; the regression test
`D3D11RendererPresentationServicesTests.DirectCompositionBaseStateChangesWhenStaticImageTransformChanges`
pins it. The Loop control also moved from the A/V flyout to the persistent
transport strip as a stateful icon button with accent highlight (the duplicate
A/V Loop checkbox was removed). Automated validation passed: Release build with
0 warnings/errors, **438 tests**, `dotnet format` verification, and
Core/WPF/presentation-parity gates all green; the Direct3D11 nupkg + snupkg
creation was re-verified for the split-state fix; focused final review PASS.
The manual static-image Include Overlays off/on, loop icon, and both-renderer
checks remain the only pending gate; the checklist is in the Stage 030A section
below.

On 2026-08-17, Stage 030A was closed after the latest manual smoke passed. The
static-image split-path regression, Include Overlays off/on behavior, loop icon,
flyout interactions, fullscreen/secondary presentation, and visual equivalence on
the Direct3D11 `NativeHwndOverlay` and software `WpfComposited` paths were confirmed.
The completed stage is archived at
`roadmap/complete/030A_COMPLETED_VisualTransforms.md`.

Later on 2026-08-17, the first engineering-audit reliability follow-up serialized
natural-end loop completion with host transport operations and made video/CDG no-op
seek completion deterministic. Release validation passed with 0 warnings/errors and
449 tests, including stale-end, host-cancelled loop, settle-tolerance, small-delta
asynchronous settle, latest-wins, no-op video/CDG, and terminal pending-seek coverage.

The next engineering-audit follow-up on 2026-08-17 serialized `Dispose` with reset and
admitted control work, added pure Soft Karaoke metadata tests, and added malformed plus
successful minimal DLS-to-SF2 fixtures. The DLS fixture exposed and fixed structure
field-offset errors, an invalid 64-bit SF2 RIFF-size write, and a declared RIFF size
that omitted LIST type bytes. Release validation passed with 0 warnings/errors and
460 tests.

The next follow-up added fake-backed integration coverage for the real
`MediaPresentationCoordinator` (surface lifecycle, retained refresh, frame forwarding,
and successful device-loss recovery), documented the software backend's stateless
contract, and replaced exception-throwing WPF `PresentationMode` callbacks with
deferred coercion. An STA WPF regression test confirms the applied mode remains stable
through in-place replacement and the latest deferred mode applies after explicit close.
Release validation passed with 0 warnings/errors and 464 tests.

On 2026-08-18, the first slice of Stage 030B (silence trimming and media analysis
cache) was implemented and validated while the stage remains active. The slice adds
the public `SilenceTrimOptions` / `SilenceRegion` / `MediaAnalysisResult` records,
internal pure edge-trim calculation and result validation, a versioned local-file
cache key plus a single-version bounded (256-entry) LRU `MediaAnalysisCache`, and
`MediaPlayerConfiguration.SilenceTrim` (disabled by default), covered by 17 focused
`SilenceTrimAnalysisTests` cases. No analyzer scan or transport/load/seek/end
behavior is wired yet, so no runtime manual smoke applies to this slice. Release
validation passed: build with 0 warnings/errors, 481 tests, `dotnet format`
verification, Core/WPF/presentation-parity gates, and git diff check all green.

On 2026-08-18, the second slice of Stage 030B was implemented and validated while the
stage remains active. Enabled local plain non-tracker audio now runs a cancellable
sequential 0.1-second silence scan inside the existing background
normalization-analysis task (samples taken at actual BASS decode byte positions;
decode cursor captured and restored in `finally`; `CancelNormalizationAnalysis`
joined before handle reuse/free; partial/faulted/stale scans not cached). The
existing normalization cache/math is preserved — a normalization-cache hit skips
peak rescans but still performs a missing silence scan. Validated results use
`SilenceTrimCalculator` and the process-lifetime shared bounded 256-entry
`MediaAnalysisCache` registered into each DI container, surviving player `Reset`;
a cache hit applies the stored gain and skips rescanning. CDG-paired opens disable
silence analysis; video, archives, tracker/MOD, and live/stream remain excluded;
transport/duration/seek/loop/end trimming is still not implemented, so playback
behavior is unchanged. Added `SilenceRegionDetector` tests and `AudioPipeline`
analysis gating tests — 30 focused 030B cases total (17 existing + 11 detector + 2
gating). Release validation passed: build with 0 warnings/errors, 494 tests,
`dotnet format` verification, Core/WPF/presentation-parity gates, and git diff check
all green. The real native-file analyzer smoke (scan completion, cursor restore,
reopen cache hit, gain unchanged) was not run and remains pending; fully silent
media can produce zero playable duration, which the transport slice must handle.

On 2026-08-18, the third slice of Stage 030B (plain-audio transport application) was
implemented and validated while the stage remains active. Trim applies to transport
only when a validated `MediaAnalysisResult` is already a cache hit synchronously
during a local plain non-tracker audio `Open`; a late background scan only warms the
cache for a subsequent open and never tightens duration during active playback.
`AudioPipeline` maps effective duration/position, effective→source seek, and
progress/end publication; `AudioEngine` positions `Play` at `PlayableStart`,
suppresses the native source-end loop for an active trim, uses a BASS
`Position | Mixtime | Onetime` sync at `PlayableEnd`, stops at the custom end, and
custom loops seek to `PlayableStart` with the `BASS_POS_FLUSH` raw flag `0x1000000`
(ManagedBass omits the enum member). Tempo changes do not require rearming the
position sync. CDG-paired opens analyze `false`; video, archives, tracker/MOD, and
live/stream remain excluded; disabled/default behavior is unchanged. Added 14
`AudioPipelineTrimTests` transport cases — 44 focused 030B cases total (17 + 11 + 2
+ 14). Release validation passed: build with 0 warnings/errors, 508 tests,
`dotnet format` verification, Core/WPF/presentation-parity gates, and git diff check
all green. The real native-file smoke (cache-warm then reopen; trimmed leading
start; exact tail end; repeated custom loops; tempo change mid-play;
seek-to-effective-end then Play; normalization unchanged; exclusions/default path
unchanged) was not run and remains pending; the first uncached `Open` only warms the
cache (a future explicit pre-analysis / prepared-next path supplies first-play
cache).

After the 2026-08-18 Stage 030B transport slice, the Player validation host gained
the `--trim-silence` startup flag: it sets
`MediaPlayerConfiguration.SilenceTrim = new SilenceTrimOptions { Enabled = true }`
and the status line reports `Silence trim=True`. The automated gate was re-verified
after the host flag: Release build 0 warnings/errors, 508 tests, `dotnet format`
verification, Core/WPF/presentation-parity gates, and git diff check all pass. The
native-file silence-trim smoke (cache-warm then reopen; trimmed leading start;
exact tail end; repeated loops; tempo change; seek-to-effective-end then Play;
normalization unchanged; exclusions/default) has not been run and remains pending;
the stage stays active. See the Stage 030B smoke section below.

---

## 1. Build And Boundary

Run from the repository root.

```powershell
dotnet restore FatzBomb.MediaControl.sln
dotnet build FatzBomb.MediaControl.sln --no-restore
dotnet build -c Release FatzBomb.MediaControl.sln --no-restore
.\scripts\check-core-boundary.ps1
.\scripts\check-wpf-boundary.ps1
.\scripts\check-presentation-parity.ps1
```

Expected:

- Debug build passes.
- Release build passes.
- Core boundary check passes.
- WPF boundary check passes.
- Presentation parity check passes.
- No WPF adapter references to renderer/pipeline/internal CDG/D3D11 presentation code.
- Core does not reference WPF UI/bitmap types.
- Core configuration contains no concrete renderer selection or DXGI present-mode types.

The normal Windows CI gate `.github/workflows/ci.yml` runs on `push` and `pull_request`.
It installs the .NET 10 SDK, restores, Release-builds, runs tests, runs all three
boundary scripts, and runs `dotnet format --verify-no-changes --no-restore`. The native
smoke workflow stays private and manual; see the note below.

For native distribution changes, run the package-install/publish smoke test on
Windows x64 with a host-owned native directory:

```powershell
.\scripts\test-windows-native-publish.ps1 `
  -NativeLibraryPath ".\FatzBomb.MediaControl.Player\native"
```

The default is a self-contained `win-x64` publish. The script packs the four managed
packages (Core, Windows, Rendering.Direct3D11, and the Windows.Direct3D11 bundle) into
an isolated local feed, installs the integration package into a generated
host, verifies managed assembly resolution, first starts with the caller-supplied
directory, then stages the supplied DLLs under `runtimes/win-x64/native` and starts
again without explicit paths. Use
`-FrameworkDependent` only when intentionally testing a machine with the .NET 10
Windows Desktop runtime installed. Artifacts are written beneath `artifacts/native-smoke/`.

`FatzBomb.MediaControl.Wpf` is also packable (six packable packages in total — Core,
Windows, Rendering.Direct3D11, Rendering.Software, the Windows.Direct3D11 bundle, and
Wpf) but is not part of the native smoke pack set. The leaf x64 packages
`FatzBomb.MediaControl.Windows` and `FatzBomb.MediaControl.Rendering.Direct3D11` ship
`buildTransitive` guards in their nupkgs, so a consumer that explicitly targets a
non-x64 `RuntimeIdentifier` fails the build with an MSBuild error.

The private-runner CI entry point is `.github/workflows/windows-native-smoke.yml`.
It is intentionally manual `workflow_dispatch` only and requires a private
self-hosted runner labelled `self-hosted`, `Windows`, `X64`, and `fatzbomb-native`.
Configure `FATZBOMB_MEDIA_NATIVE_PATH` as a repository variable or as an environment
variable visible to the runner service account. The workflow never uploads smoke
artifacts because they contain host-licensed native DLLs. Provisioning steps are in
`docs/PRIVATE_NATIVE_RUNNER.md`.

After Cdg naming work, also run the stale legacy Cdg-name guard from the active roadmap.

Expected:

- Zero results, unless intentionally preserved in old historical notes.

---

## 2. Runtime Test Setup

Use a consistent test set so before/after results are comparable.

Recommended media set:

- Audio-only file
- MP3 + CDG pair
- ZIP containing audio + CDG
- Standalone CDG, if supported
- Video with audio
- Video without audio or muted/silent video
- Image-only file
- Audio + CDG with auto-detected image background
- Audio + CDG with sidecar video background
- KAR file with embedded karaoke lyrics
- MID file with embedded karaoke lyrics (if available)
- MOD, S3M, XM, and IT tracker files
- ZIP containing KAR + image background
- ZIP containing audio + video background
- Image file with explicit background path
- 23.976 / 23.98fps video
- 29.97fps video
- 60fps video, if available
- Long-GOP video, if available
- Video with SRT/ASS sidecar subtitles
- MKV/MP4 with embedded text subtitles

Run runtime validation in **Release x64** with host-supplied BASS and FFmpeg DLLs
present. See `docs/NATIVE_DEPENDENCIES.md` for supported versions, approved sources,
licensing cautions, and search paths.

The Player uses Direct3D11 by default. Launch it with `--software` to select the
software renderer, software video decoding, and the required `WpfComposited`
presentation mode:

```powershell
dotnet run --project FatzBomb.MediaControl.Player -c Release -- --software
dotnet run --project FatzBomb.MediaControl.Player -c Release -- --software "D:\path\to\media.mp4"
```

The independent Stage 029B startup flags validate per-load pitch/tempo resets (closed
2026-08-08, including the manual smoke with both flags):

```powershell
dotnet run --project FatzBomb.MediaControl.Player -c Release -- --reset-pitch-on-load
dotnet run --project FatzBomb.MediaControl.Player -c Release -- --reset-tempo-on-load
dotnet run --project FatzBomb.MediaControl.Player -c Release -- --reset-pitch-on-load --reset-tempo-on-load
```

Stage 029C AutoHide is enabled by default on the Player host (closed 2026-08-08,
including the manual smoke). Use `--no-auto-hide` for opt-out validation,
or combine the default with `--software` for composited validation:

```powershell
dotnet run --project FatzBomb.MediaControl.Player -c Release
dotnet run --project FatzBomb.MediaControl.Player -c Release -- "D:\path\to\media.mp4"
dotnet run --project FatzBomb.MediaControl.Player -c Release -- --software
dotnet run --project FatzBomb.MediaControl.Player -c Release -- --no-auto-hide
```

Stage 030A (base-visual transform pipeline) is complete; its final manual smoke
passed on 2026-08-17. The Player host exposes a persistent bottom chrome
(timeline/transport/fullscreen/volume/status) plus one-at-a-time **Sync /
Video / Audio / A-V** flyout buttons (only one open at a time; the active button
is highlighted). The **Video flyout** carries live, Core-backed controls — HUE,
SATURATION, BRIGHTNESS (−0.25–0.25), CONTRAST (0.5–1.5), RED GAIN / GREEN GAIN /
BLUE GAIN (0–2) sliders (no Lightness, no color wash, no preset type), an
**Include Overlays** checkbox, and Reset Visual. The Audio flyout has
pitch/tempo/normalize plus Reset Pitch on Load / Reset Tempo on Load and a
10-band **EQ** (on/off, ±12 dB per band, Flat; Stage 020A); the **Next** flyout
(2026-10-17) has Choose... / Play next now / Clear for the prepared-next slot with
its state line, Auto-next, a FADE length (0-10 s, 0 = instant), Fade out / Fade in /
Cancel for the current item (3 s when FADE is 0), and a URL box with Open plus the
stream title / buffering line (Stages 031A, 031B, 014A); the A/V
flyout has Auto Play, Auto Hide, the secondary/projector window toggle, display
mode, presentation mode, and CDG background opacity; the Sync
flyout has the per-item A/V/CDG/subtitle offsets. Loop Playback is a stateful
icon button on the persistent transport strip (accent highlight when on,
`ToggleLoopCommand`); the duplicate A/V flyout Loop checkbox was removed. Every
control works without any startup flag. The flags below remain as compatibility initializers that seed
their corresponding controls; exercise them on both renderer paths
(`NativeHwndOverlay` D3D11 and `--software` `WpfComposited`):

```powershell
dotnet run --project FatzBomb.MediaControl.Player -c Release -- --visual-transform-demo
dotnet run --project FatzBomb.MediaControl.Player -c Release -- --visual-transform-demo "D:\path\to\media.mp4"
dotnet run --project FatzBomb.MediaControl.Player -c Release -- --software --visual-transform-demo
```

Record:

```text
Build:
GPU:
Monitor refresh rate:
Primary display:
Secondary/projector display:
Windows scaling/DPI:
Media file:
Scenario:
Visible glitches:
Logs:
```

### Stage 030A visual-transform smoke (manual, passed 2026-08-17)

There is no bundled media fixture — supply real media (e.g. a video with audio,
an image file, an audio+CDG pair, and a KAR/MID file with embedded karaoke
lyrics) so the base visual and the text/overlay layers can be observed together.
Exercise the flyout controls directly (no startup flags required); the flags
seed the same controls for compatibility. Run both renderer paths:

```powershell
dotnet run --project FatzBomb.MediaControl.Player -c Release
dotnet run --project FatzBomb.MediaControl.Player -c Release -- --software
dotnet run --project FatzBomb.MediaControl.Player -c Release -- --visual-transform-demo
dotnet run --project FatzBomb.MediaControl.Player -c Release -- --software --visual-transform-demo
```

Expected on both paths:

- **Each transform independently** — open the Video flyout and set one control
  at a time (RED GAIN, GREEN GAIN, BLUE GAIN, hue, brightness, contrast,
  saturation). With **Include Overlays off (default)**, confirm only the base
  visual (main video/image and background video/image) changes. The status
  line's `Visual transform=` flips to `ACTIVE` when any member is non-neutral
  (gains not `1`, hue/brightness not `0`, contrast/saturation not `1`) and back
  to `IDENTITY` after Reset Visual.
- **Contrast vs brightness distinction** — with Include Overlays off, CONTRAST
  is a TV-style gain: `rgb *= Contrast`, so black stays black and mid/high
  values scale toward it (contrast below 1 flattens toward black, above 1
  boosts). BRIGHTNESS is an additive offset: `rgb += Brightness`, so black
  lifts/falls too. Verify both independently and together (e.g. contrast 0.5
  with brightness +0.1 darkens but lifts black; the fixed order applies contrast
  first, then brightness).
- **Gain removes a channel** — RED GAIN at `0` removes red from the base
  visual (the result skews toward cyan/aqua); GREEN GAIN at `0` removes green
  (magenta-leaning); BLUE GAIN at `0` removes blue (yellow-leaning).
- **Green=0 + Blue=0 → red-only** — with GREEN GAIN and BLUE GAIN both at `0`,
  the base visual renders red-only (red channels survive, green/blue are
  removed).
- **Red=0 → cyan/aqua** — with RED GAIN at `0` (green/blue at `1`), the base
  visual renders cyan/aqua (red removed, green/blue survive).
- **Each channel boost** — set each gain individually to `2`: the matching
  channel visibly boosts toward its pure hue and clips at 1; the other channels
  are unaffected.
- **All gains 0 = black** — RED/GREEN/BLUE at `0` renders the base visual
  black.
- **Hue / saturation independently** — hue rotation (YIQ) shifts colors without
  changing gray; saturation (Rec.709 luma pivot) desaturates to grayscale at 0
  and boosts at 2; each works alone and combined.
- **Practical brightness / contrast** — brightness −0.25…0.25 and contrast
  0.5…1.5 sliders visibly lighten/darken and flatten/boost the base visual;
  identity positions (0.00 brightness, 1.00 contrast) are unchanged.
- **No Lightness / no color wash** — there is no Lightness slider or model
  member and no color-wash/preset control; the pipeline has no HSL-lightness or
  wash stage (verify no stale control in the flyouts and that the status line
  never reports one).
- **Include Overlays off/on** — with Include Overlays **off**, the transform
  never reaches wash, CDG overlay, subtitle, or karaoke text at any
  gain/hue/brightness/contrast/saturation combination; overlay text stays
  independently colored, alpha is preserved on semi-transparent backgrounds, and
  no extra composition pass runs. Check **Include Overlays on** with a
  non-identity transform (e.g. RED GAIN 0): the **completed composition** —
  wash, CDG overlay, and subtitle/karaoke text included — transforms as a whole
  with no visual double-darkening of the base visual. With Include Overlays on
  but all-neutral color math, output is byte-identical to identity.
- **Static-image transform (split-path regression)** — load a **static image**
  (no video), keep **Include Overlays off**, and change a transform control
  (e.g. RED GAIN): the still must update immediately to the new transform. This
  is the root-cause scenario fixed 2026-08-11 — the DirectComposition split
  path's cached base state omitted `BaseVisualTransform`, so static-image
  transform changes were skipped while video (whose revisions changed every
  frame) masked the bug. Verify the same live update with Include Overlays **on**
  (whole-composition postprocess) and after switching image → video → image.
- **Reset Visual** — restores every slider to identity (gains `1/1/1`,
  hue/brightness `0`, contrast/saturation `1`, Include Overlays unchecked), the
  presented output matches an identity launch, and no stale transform survives
  the next load.
- **Live Auto Hide toggle** — toggle the Auto Hide checkbox in the A/V flyout
  at runtime across visual → audio-only media, replacement, stop, pause, and
  natural end; the Stage 029C policy follows the checkbox (shown on first
  presented frame, hidden on explicit stop/close/non-loop end, shown while
  paused and looping, never stuck hidden) and the status line's `AutoHide=`
  value tracks it.
- **Loop icon (transport strip)** — the Loop control lives on the persistent
  transport strip as a stateful icon button: click it and the accent highlight
  turns on with `LoopPlayback` on (repeating playback loops the media); click
  again and the highlight clears with looping off. There is **no** Loop
  checkbox in the A/V flyout (duplicate removed 2026-08-11). Verify the loop
  state survives play/pause/stop and media replacement, and that the icon's
  highlight matches the status-line/repeat behavior.
- **Reset Pitch/Tempo on Load (runtime toggles)** — Audio flyout: with both
  checkboxes off, set non-default pitch/tempo (e.g. pitch −3, tempo 115), load
  another track and confirm the values carry over. Then check one or both
  checkboxes, set non-default values again, load, and confirm pitch resets to 0 /
  tempo resets to 100 exactly for the enabled option(s) while the other carries
  over — the two toggles are independent. The WPF sliders visibly return to
  neutral via `PitchChanged` / `TempoChanged` (`MediaChangeReason.LoadReset`).
- **MIDI/KAR auto lyrics** — load a KAR or MID file with embedded lyrics:
  karaoke lyrics display automatically (the MIDI lyrics checkbox was removed;
  `ShowMidiLyrics = true` is always set). Include Overlays on transforms the
  karaoke text with the composition; Include Overlays off leaves it untouched.
- **Demo seed** — `--visual-transform-demo` initializes the controls to the demo
  values (`RedGain=0.85`, `GreenGain=0.95`, `BlueGain=1.2`, `HueDegrees=30`,
  `Brightness=0.05`, `Contrast=1.15`, `Saturation=1.2`; no lightness, Include
  Overlays off); the base visual visibly transforms and the status line reads
  `Visual transform=ACTIVE`.
- **Flyout mechanics** — open each of Sync / Video / Audio / A-V / Next; only one is
  visible at a time; the active button is highlighted; toggling the active
  button again closes the flyout; starting playback or entering fullscreen
  closes it. All five flyouts render correctly on both renderer paths.
- **Fullscreen / secondary / multi-target** — toggle fullscreen (button or
  double-click) with a transform active and confirm the transform persists on
  the fullscreen output; open the secondary/projector window from the A/V
  flyout and confirm it receives the same final Core-composed frame — with
  Include Overlays on, both targets must show the whole-composition transform
  (D3D11 composites per-target, and the DirectComposition overlay split is
  disabled in this mode). Closing/re-opening the secondary window while playing
  must not disturb the primary output.
- **Both renderers** — repeat the gain/include-overlays checks on the Direct3D11
  `NativeHwndOverlay` path **and** the `--software` `WpfComposited` path; the
  two paths must produce visually equivalent per-channel-gain and
  whole-composition results.
- Replacement/resize/mode switch stays correct: changing media and resizing the
  window keep the transform applied to the new base visual with no stale layers
  (mode switch is not available with `--software`; the presentation mode is
  pinned to `WpfComposited`).
- Compare against an identity launch (all controls at defaults, or Reset
  Visual): output is unchanged from pre-stage behavior — no wash, no hue shift,
  no composition/shader-path change.
- Status line reports the live state, e.g.
  `Renderer: Direct3D11 | Presentation: NativeHwndOverlay | AutoHide=True |
  Visual transform=ACTIVE | Load resets: Pitch=False, Tempo=True | ...`
  (or `Renderer: Software` with `--software`).

### Stage 030B silence-trim smoke (manual, pending)

The Player validation host gained the `--trim-silence` startup flag after the
Stage 030B transport commit: it sets
`MediaPlayerConfiguration.SilenceTrim = new SilenceTrimOptions { Enabled = true }`
and the status line reports `Silence trim=True`. The native-file smoke has not
been run yet and remains pending:

```powershell
dotnet run --project FatzBomb.MediaControl.Player -c Release -- --trim-silence "D:\path\to\audio.mp3"
dotnet run --project FatzBomb.MediaControl.Player -c Release -- --software --trim-silence "D:\path\to\audio.mp3"
```

Procedure and expected:

- **Cache warm** — the first open of a local plain non-tracker audio file only
  warms the analysis cache; wait for the `Silence analysis complete` diagnostic
  (Info level, `AudioEngine`), then close and reload the same file so the cached
  transport window activates.
- **Trimmed leading start** — playback begins at the playable start, not the
  source start.
- **Effective duration/seek** — duration and seek positions reflect the effective
  (trimmed) window; seeking maps to the correct source position.
- **Exact tail end** — playback stops at the playable end; the trimmed tail does
  not play.
- **Repeated loops** — custom loops restart at the playable start each time.
- **Tempo change** — changing tempo mid-play does not break the end sync.
- **Seek-end then Play** — seeking to the effective end and pressing Play behaves
  consistently (no out-of-window playback).
- **Normalization unchanged** — gain results match the non-trimmed path.
- **Exclusions/default** — CDG-paired, video, archive, tracker/MOD, and
  live/stream inputs are unaffected; without `--trim-silence` (default) behavior
  is unchanged and the status line reads `Silence trim=False`.

---

## 3. Routine Player Smoke Tests

1. Audio file plays.
2. Volume works.
3. Pitch works.
4. Tempo works.
5. Normalization works.
6. MP3 + CDG renders CDG lyrics.
7. ZIP audio + CDG renders CDG lyrics.
8. Standalone CDG plays with clock-backed timing, if supported.
9. Video plays.
10. Video tempo keeps audio/video sync.
11. Video without audio uses clock-backed timing correctly.
12. Image-only renders as a still visual.
13. Audio + CDG auto-detected image background appears behind lyrics.
14. Audio + CDG sidecar video background appears behind lyrics.
15. Seek works for audio.
16. Seek works for CDG.
17. Seek works for video.
18. Seek works with sidecar background cases.
19. Primary native HWND output resizes cleanly.
20. Fullscreen WPF UI chrome overlay appears above the native HWND without becoming a media layer.
21. Secondary/projector output receives the same final Core-composed frame, including plain
    hardware-decoded video with no subtitles or picture adjustments: open the secondary on the
    same and on another monitor, then play, seek, and pause (ENG-015). Repeat with
    `--software`, for video and for CD+G, and resize the secondary (ENG-016).
22. Closing secondary output while playing does not disturb primary output.
22a. Renderer choice (Stage 036A): the default reports `Direct3D11 (auto): GPU, <adapter>`;
    `--renderer=warp` plays video and CD+G on WARP through the native window (status and Log
    say WARP); `--renderer=software` behaves as `--software`; an unknown value logs a
    warning and plays as `auto`.
23. Re-opening secondary output while playing resumes presentation correctly.
24. Repeated media-type switches leave no stale layer artifacts.
25. Stop clears or hides the native HWND correctly, with no stale frame/layer.
26. Playing after Stop immediately restores video presentation without requiring a seek.
27. Close/dispose while playing does not hang or throw unhandled exceptions.
28. Sidecar SRT/ASS subtitles render on time through seek, pause/resume, and media switches.
29. Embedded text subtitle tracks render on time; select another available track and disable subtitles.
30. Subtitle font, size, outline, and shadow settings render correctly on software and hardware video paths.
31. A/V, CDG, and subtitle sync controls adjust their own timeline by 100ms per step without interrupting playback.
32. Sync Reset returns all three controls to zero; loading a new media item also resets them.
33. KAR/MID karaoke lyrics display with syllable wipe highlighting, scrolling, and page transitions.
34. MIDI/KAR karaoke lyrics display automatically (the host always sets `ShowMidiLyrics = true`; there is no MIDI Lyrics checkbox) and ordinary SRT/ASS subtitles are unaffected.
35. Backward seek on MIDI karaoke immediately updates lyrics, highlighting, and scroll position.
36. Karaoke lyrics maintain 16:9 aspect ratio when window is resized to non-16:9 dimensions.
37. Image file with explicit background path renders the background behind the image.
38. ZIP archive with audio + video opens as audio main with video as background (not video main).
39. ZIP archive with video only opens as video main.
40. Audio-only media auto-detects matching image and video sidecars.
41. MIDI with no karaoke lyrics auto-detects matching image and video sidecars.
42. `ignoreSidecar: true` and `MediaFile.BackgroundAutoDetect = false` each disable automatic sidecar loading.
43. An image main ignores matching sidecars but accepts an explicitly supplied background.
44. A video main does not load a matching video sidecar or explicit background.
45. MOD, S3M, XM, and IT tracker files seek forward and backward while stopped, paused, and playing.
46. Regular `MediaSurface` renders video, CDG, and MIDI/KAR in `NativeHwndOverlay` and `WpfComposited` modes.
47. `WpfComposited` obeys normal WPF clipping, transforms, opacity, z-order, and interactive overlay input.
48. `BackgroundSurface` renders color, image, and muted looping video backgrounds in both modes.
49. `BackgroundSurface` overlay content remains above the background in both modes.
50. `KaraokeSurface` renders CDG and MIDI/KAR on an opaque 4:3 canvas in both modes.
51. `KaraokeSurface` rejects video and non-karaoke audio without starting playback.
52. Changing WPF presentation mode before load succeeds; changing it while media is open is rejected.
53. Video playback resumes from the settled seek visual without skipping ahead or replaying earlier frames.
54. An authored MOD/S3M/XM/IT pattern loopback moves the seekbar back to its first-pass loop position even when whole-file looping is disabled.
55. With Silent Drag on (Player Audio flyout), a seek-bar drag is silent while it moves, plays again within about 120 ms of holding still and on release, for audio, CD+G, and video soundtracks; user volume is unchanged.

---

## 4. CDG-Specific Validation

Run with MP3 + CDG and ZIP audio + CDG.

Expected:

- Lyrics render at the correct position.
- CDG colors are correct.
- CDG transparent color behavior is correct.
- CDG wash/background layer appears correctly.
- CDG overlay remains sharp after resize/fullscreen.
- CDG timing stays synced with audio.
- Seeking updates CDG state correctly.
- Repeated seek does not leave stale lyrics or wrong colors.
- Sidecar image/video remains behind lyrics.
- Secondary output matches primary output.

After CDG-related code changes, watch for:

```text
wrong palette
wrong transparent color
stale tiles
missed dirty regions
lyrics lagging audio
lyrics jumping after seek
background/wash not clearing
secondary output mismatch
```

---

## 4a. MIDI/KAR Karaoke Validation

Run with KAR files and MID files containing embedded lyrics.
Also test automatic image/video sidecars for MIDI/KAR files with and without lyrics, plus an image main whose background is supplied explicitly.

Expected:

- Lyrics display in complete readable lines, not individual syllables.
- Syllable wipe highlighting smooth-transitions per syllable; wipe follows MIDI marker timing shifted ~400ms earlier.
- Up to five lines visible: two previous, active centered, two upcoming.
- Non-active lines fade and scale based on distance from active line (active 1.0, adjacent 0.8, outer 0.6). True transparency over detailed backgrounds — non-active text retains color but exposes background through alpha.
- Normal scroll always takes 1500ms. In fast songs with tight gaps the full scroll window centers over the gap, allowing overlap with outgoing highlighting and incoming presentation.
- Scroll uses smooth position/opacity animation; no abrupt line jumps.
- Page changes (clear-screen/new-paragraph): fade-out (cubic ease-in, up to 1s) and fade-in (cubic ease-in-out, up to 1s). Sequential when room allows; crossfade when gap is tight, including sub-second gaps. Incoming page may zoom from 1.5x to 1.0x.
- First page fade-in completes before first lyric/countdown. Intro begins at 1s if lead-in permits.
- Four countdown arrows appear above a line when gap >6s from previous. Red to green, one per second; final-second fade. Arrows are not lyric text.
- Metadata intro/end cards: centered smoked-glass panel with title, artist, credits, year, length, key, tempo, timing, copyright. When first lyrics are before 8s, split top/bottom translucent bands are used instead.
- Fallback metadata (filename+duration) renders centered card at intro only; no end card.
- End card appears after lyrics fade, if embedded metadata exists and duration permits min 3s window.
- Karaoke text uses independent font/color/outline/shadow from ordinary subtitles. Default font size 72.
- Karaoke lyrics maintain 16:9 aspect ratio in non-16:9 windows.
- Backward seek immediately updates lyrics, highlighting, wipe progress, scroll, page/scale state, metadata opacity, countdown, and end-lyric fade.
- Forward seek updates to correct position.
- Seek while paused updates lyrics on slider release.
- MIDI lyrics display is always enabled in the Player host (`ShowMidiLyrics = true`; no checkbox) without affecting SRT/ASS subtitles.
- Image sidecars display behind MIDI/KAR lyrics; archive KAR+image renders correctly.
- No excessive composition logs or no-op presents during static lyrics.
- Karaoke lyrics do not flash during window resize.
- Karaoke lyrics do not freeze video playback when video subtitles are active.
- AudioEngine reasserts/reinitializes BASS device if play fails after extended idle (no audio stall).

---

## 5. Video Timing / Stutter Validation

Use this after changes to:

- `VideoPipeline`
- `VideoPresentationTiming`
- `PrecisionWaiter`
- video decode/presentation loops
- seek pump
- background video loop
- native presentation coordinator
- DirectComposition/native HWND presentation
- thread scheduling or wait logic

Test at least:

```text
23.976 / 23.98fps video
29.97fps video
60fps video if available
video with audio
video without audio
audio + CDG + video background
secondary/projector output
fullscreen/resize
seek/scrub stress
```

For 23.98fps playback, expected healthy log shape:

```text
adaptive=hold-source-cadence
lateDrops=0
selectionSkips=0 or near-zero during steady playback
queued remains healthy
timer=high-res when available
avgTickGap close to source frame duration
```

Acceptable exceptions:

- Small `selectionSkips` spike immediately after seek.
- Small spike after resize/fullscreen if a real loop gap occurred.
- Occasional single skip if the machine is under real external load.

Not acceptable:

- `selectionSkips` steadily increasing during normal playback.
- Visible stutter while `lateDrops=0`, queue is full, and decode/present/composition times are healthy.
- Adaptive lead increasing during source-cadence 23.98fps playback.
- Repeated visible hitch every few seconds with no corresponding late/drop diagnostic.
- Secondary output drifting or stuttering differently from primary.

Display-locked timing (Player A/V flyout **Display-Locked**, Stage 017A slices 4 and 5):

- The Log shows "Display-locked video timing on: ..." within a second of playback with the
  Direct3D11 renderer and native windows. With software output or WPF-composited surfaces it
  shows "unavailable", and playback uses source cadence.
- The "Display-locked timing over ..." line (every 60 s and on pause) lists refreshes per frame.
  - 24 fps on 60 Hz: 2s and 3s in equal numbers.
  - 30 on 60: all 2s.
  - 24 on 120: all 5s.
  - Presents that finished after their refresh stay near zero.

If visible stutter remains with near-zero drops/skips, capture profiling data rather than changing selector logic blindly.

Video color (ENG-019):

- The Log's "Decoding ..." line for each video names its color space, for example
  `(yuv420p, BT.709 limited range)`. "(assumed)" means the file has no color tags.
- With brightness and contrast at their defaults, an SDR video matches another player
  (Windows Media Player, mpv or VLC): blacks are black, not dark gray, and whites are not gray.
- The same file with `--software` looks the same as with the GPU (Direct3D11) path.
- An HDR10 file (the line says "HDR10/PQ, tone mapped to SDR") has normal contrast and color
  (Stage 038A; steps in `docs/roadmap/updates/038A_1`). With `--no-hdr-tonemap` it looks
  washed out, as before. HLG files are not tone mapped yet.

---

## 6. Extended Timing / Presentation Profiling

Use after significant renderer, presentation, or timing changes.

### Live counters

```powershell
dotnet-counters ps
dotnet-counters monitor --process-id <PID> --refresh-interval 1 System.Runtime
```

Record:

```text
CPU %
allocation rate
GC heap size
Gen0/Gen1/Gen2 counts
exception count
ThreadPool thread count
ThreadPool queue length
visible stutters
```

### CPU trace

```powershell
dotnet-trace collect --process-id <PID> --profile cpu-sampling --output .\profiling\video-timing-cpu.nettrace
```

Capture during:

- 23.98fps playback
- seek/scrub stress
- fullscreen/resize
- secondary output playback

### GC / memory snapshot

```powershell
dotnet-gcdump collect --process-id <PID> --output .\profiling\before-longrun.gcdump
```

Run a 10–20 minute long-run with load/seek/stop/reload cycles.

```powershell
dotnet-gcdump collect --process-id <PID> --output .\profiling\after-longrun.gcdump
```

Look for retained growth in:

```text
CancellationTokenSource
CdgRenderSnapshot
byte[] / int[] CDG buffers
D3D/Vortice wrapper objects
event handler targets
queued frames
Task / Timer / CancellationToken registrations
```

### Windows presentation trace

Use for DirectComposition, DWM, GPU, native HWND, resize/fullscreen, or secondary-output stutter.

```powershell
wpr -start GeneralProfile -filemode
```

Run the presentation scenario, then:

```powershell
wpr -stop .\profiling\presentation.etl
```

Inspect with Windows Performance Analyzer or GPUView when needed.

---

## 7. Error Handling / Logging Validation

Use after changes to structured logging, host-facing errors, load/open paths, native DLL loading, archive handling, FFmpeg/BASS handling, or background media loading.

Test failures intentionally:

1. Missing media file.
2. Unsupported file type.
3. Corrupted/invalid media file.
4. Missing BASS native DLL.
5. Missing FFmpeg native DLL.
6. Corrupted ZIP/archive.
7. ZIP with missing audio or missing CDG pair.
8. Sidecar background missing.
9. Sidecar background unsupported or corrupted.
10. Video decode failure.
11. Device removed/reset scenario if reproducible.
12. Hardware video decode device-loss scenario: trigger GPU device loss during hardware-decoded video playback and verify software fallback keeps audio/CDG uninterrupted, hardware auto-restores when the device returns, and 005B's presentation recovery doesn't re-adopt the dead device (no feedback loop).

Expected:

- Failure does not crash the process.
- Host receives useful failure status.
- Logs include enough detail for diagnosis.
- User-facing error can distinguish likely cause.
- `Debug.WriteLine` is not the only error surface.
- Repeated failure/retry does not leave stale state.

---

## 8. State / Threading / Async Validation

Use after changes to:

- `MediaControlOperationQueue`
- `MediaState`
- `PlaybackCoordinator`
- `MediaSession`
- `IClockSource`
- `ClockAudioEngine`
- `IAudioEngine`
- pipeline start/stop/pause/seek behavior
- fire-and-forget cleanup

Stress cases:

1. Load → play → pause → play → stop.
2. Load while previous media is playing.
3. Rapid seek while playing.
4. Rapid seek while paused.
5. Stop during seek.
6. Dispose during playback.
7. Dispose during seek.
8. Switch audio → CDG → video → image → audio repeatedly.
9. Toggle secondary output while playing.
10. Toggle fullscreen while playing.
11. Close secondary output while seeking.
12. Trigger PlayOnLoad repeatedly.

Expected:

- No deadlocks.
- No UI freeze.
- No unobserved task exceptions.
- No stale playback state.
- No stale media layers.
- No cross-thread UI exceptions.
- Public events do not require unsafe WPF access from background threads.
- Sync methods do not block longer than expected.

If playback stalls, capture:

```powershell
dotnet-stack report --process-id <PID> > .\profiling\stacks-during-stall.txt
```

---

## 9. Audio / Clock Validation

Use after changes to audio, `IClockSource`, standalone CDG, silent video, or the `ClockAudioEngine` compatibility adapter.

Test:

1. Audio-only.
2. Audio + CDG.
3. Standalone CDG.
4. Video with audio.
5. Video without audio.
6. Pitch changes while playing audio.
7. Tempo changes while playing audio.
8. Normalization enabled/disabled.
9. Seek with audio.
10. Seek without audio.
11. Stop/reset with audio.
12. Stop/reset without audio.

Expected:

- Real audio content accepts volume/pitch/tempo/normalization.
- Non-audio clock-backed content does not pretend unsupported effects succeeded.
- Position/duration remain coherent.
- Stop resets transport state.
- Seek does not desync video/CDG.

---

## 10. Renderer / Presentation Validation

Use after renderer, DirectComposition, native HWND, D3D11, swap-chain, resize, or compositor changes.

Test:

1. Primary DirectComposition path.
2. Native HWND fallback path, if available.
3. Secondary/projector output.
4. Resize primary while playing.
5. Resize secondary while playing.
6. Fullscreen toggle.
7. Audio-only clear/idle behavior.
8. Image-only.
9. Video-only.
10. Audio + CDG.
11. Video + CDG.
12. CDG + image background.
13. CDG + video background.
14. Stop/clear.
15. Close/reopen output surfaces.

Expected:

- No stale frame after stop/close.
- No stale layer after media-type switch.
- No crash on resize.
- No flicker or black frame beyond expected attach/first-present behavior.
- Primary and secondary outputs match.
- WPF chrome remains outside Core media layers.
- Core owns composition and target fan-out.

---

## 11. Automated Architecture Checks

`check-wpf-boundary.ps1` verifies:

- Core does not reference WPF UI/bitmap types.
- WPF adapter does not reference renderer/pipeline/internal CDG/D3D11 presentation code.
- WPF may use safe public control concepts but not Core implementation details.

`check-presentation-parity.ps1` verifies:

- WPF primary and secondary surfaces both attach through neutral
  `MediaNativeSurfaceDescriptor.Win32Hwnd` payloads.
- Core retains attached surfaces in one registry.
- D3D11 compositor captures/draws/presents all active final targets through one target plan.

Run both scripts after any WPF/Core boundary, renderer, surface, or presentation change.

---

## 12. Long-Run Stability Validation

Run before considering a major stage complete.

Recommended long-run:

```text
10–20 minutes audio + CDG
10–20 minutes video
10–20 minutes audio + CDG + video background
repeated seek/stop/reload cycles
secondary output open/close
fullscreen toggle
```

Expected:

- Heap stabilizes.
- Gen2 GC does not climb continuously during normal playback.
- No increasing handle count.
- No steady allocation growth.
- No increasing exception count.
- No growing stutter over time.
- Stop/dispose/reload remain responsive.

---

## 13. Completion Criteria For Major Stages

A major stage is not done until:

- Debug build passes.
- Release build passes.
- Boundary script passes.
- Presentation parity script passes.
- Relevant smoke tests pass.
- Relevant extended tests pass.
- No stale legacy Cdg names remain.
- CDG, video, audio, image, sidecar background, secondary output, resize/fullscreen, and seek behavior are validated if touched.
- Any known limitations are documented.
