# Validation Checklist

Updated: 2026-08-03

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

`FatzBomb.MediaControl.Wpf` is also packable (five packable packages in total) but is
not part of the native smoke pack set. The leaf x64 packages
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
21. Secondary/projector output receives the same final Core-composed frame.
22. Closing secondary output while playing does not disturb primary output.
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
34. MIDI Lyrics checkbox toggles karaoke display on/off without affecting ordinary subtitles.
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
- MIDI Lyrics checkbox toggles display without affecting SRT/ASS subtitles.
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

If visible stutter remains with near-zero drops/skips, capture profiling data rather than changing selector logic blindly.

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
