# Stage Manifest

## Active

Stages implemented in the unattended 2026-10-16 session stay here, each
"implemented, pending manual smoke", until the owner confirms the smoke in its update note.

- `active/031A_ACTIVE_PreparedNextPlaylistSlot.md` - implemented; owner smoke passed 2026-10-17
  (Next panel; `updates/031A_1`); 30 focused tests. Player host controls for 031A/031B/014A/020A in
  `updates/031A_2`.
- `active/031B_ACTIVE_AudioFadeCrossfadeModel.md` - implemented; owner smoke passed 2026-10-17
  (fades; `updates/031B_1`, `updates/031B_2`); overlapping two-handle crossfade deferred.
- `active/014A_ACTIVE_StreamingNetworkPlayback.md` - implemented; owner smoke passed 2026-10-17
  (radio, progressive MP3, retries, MP4 refusal; `updates/014A_1`, `updates/014A_2`); HTTP audio only, network video deferred.
- `active/026B_ACTIVE_CrossPlatformPlatformServices.md` - Linux baseline implemented
  (`updates/026B_1`); native FFmpeg + Core decode validated on Linux; BASS/player smoke
  pending; macOS deferred.
- `active/026A_ACTIVE_CrossPlatformRendererBackends.md` - Vulkan slices 1-2 implemented,
  pending manual smoke (`updates/026A_1`, `updates/026A_2`: SPIR-V wash + CDG overlay);
  validated on lavapipe; swapchains/subtitles/transforms/OpenGL/Metal later.
- `active/027A_ACTIVE_UiAdapterImplementations.md` - Avalonia and WinForms slices
  implemented (`updates/027A_1`, `updates/027A_3`); slice 2 re-planned 2026-10-18 as an
  Avalonia GPU-shared surface (compositor import, no CPU copy or overlay window), not
  started; WinUI 3 deferred.
- `active/020A_ACTIVE_AudioFilterFramework.md` - filter contract + equalizer implemented;
  owner smoke passed 2026-10-17 (`updates/020A_1`).
- `active/017A_ACTIVE_DisplayRateCompositorRewrite.md` - cadence model/spec implemented
  (`updates/017A_1`); slice 2 reports the video/display cadence in the Log panel; owner
  smoke passed 2026-10-18 (`updates/017A_2`); slice 3 takes the refresh rate of the monitor
  showing the video from the renderer (Direct3D11); owner smoke passed 2026-10-18 (`updates/017A_3`);
  frame selection at present time not started.
- `active/015A_ACTIVE_BroadVideoPipelineSplit.md` - responsibility map + seek seams
  extracted (`updates/015A_1`); slice 2 moves the seek pump state machine into
  `VideoSeekPump` (now `SeekPump`, shared with CD+G per ENG-013) with tests; owner smoke passed 2026-10-17 (`updates/015A_2`).
- `active/010A_ACTIVE_SoftwareVideoUploadCopyPath.md` - copy map + benchmark + one
  full-frame copy removed; owner smoke passed 2026-10-17 (`updates/010A_1`).
- `active/018A_ACTIVE_CDGSnapshotOwnershipRedesignPooling.md` - lifetime map, counters,
  immutable snapshot reuse; owner CDG smoke passed 2026-10-17 (`updates/018A_1`).
- `active/019A_ACTIVE_VideoFrameEventArgsReuseApiRedesign.md` - lifetime matrix, written
  ephemeral contract, conformance tests; no behaviour change (`updates/019A_1`).
- `active/022A_ACTIVE_ThreadModelProfilingAndSimplification.md` - thread-ownership map and
  capture script (`updates/022A_1`); Windows baseline captured 2026-10-17; slice 2 removes
  the per-frame software and WPF buffers, the per-frame audio lock, and per-frame display-rate
  queries; owner smoke passed 2026-10-18, software video 109 -> 0.5 MB/s (`updates/022A_2`); slice 3
  reuses each scheduler's worker thread across seeks, owner smoke passed 2026-10-18 (`updates/022A_3`);
  slice 4 bounds CD+G seek replay with checkpoints and moves seek-bar drags onto the control
  queue (`SeekAsync`), pending manual smoke (`updates/022A_4`).
- `active/033A_ACTIVE_FfmpegAudioParityAndFallback.md` - slice 1 (equalizer, fades,
  normalization for video audio) implemented; owner smoke passed 2026-10-17 (quiet-opening
  film, no burst). Slice 2 (FFmpeg fallback for unclaimed audio formats) implemented,
  pending manual smoke: the owner has no such files yet (`updates/033A_1`,
  `updates/033A_2`).
- `active/034A_ACTIVE_NativeDependencyReport.md` - startup report of missing or
  wrong-version native files, implemented; owner smoke passed 2026-10-17 (`updates/034A_1`).
- `active/036A_ACTIVE_RendererFallbackChain.md` - Direct3D11 falls back from the GPU to WARP
  (Windows' software rasterizer) on its own; Core `FallbackPresentationCoordinatorFactory`
  then the software renderer; Player `--renderer=auto|gpu|warp|software`; implemented;
  owner smoke passed 2026-10-18 (`updates/036A_1`).
- `active/035A_ACTIVE_ControlOwnedSeekScrubbing.md` - seek-bar drags
  (`BeginScrub`/`EndScrub`): playback carries on, a drag resumes from the previewed frame,
  and the release seek at the last drag position is ignored, so nothing jumps; implemented;
  owner smoke passed 2026-10-17 for video drags (`updates/035A_1`).

Engineering audit follow-ups are tracked in `ENGINEERING_ISSUES.md`.

## Reverted

- original S3 shared cursor (2026-07-09) — reverted; superseded by revised S3 with drain-audio-then-seek

## Completed

- `complete/030B_COMPLETED_SilenceTrimmingAnalysisCache.md` — versioned media-analysis cache, background silence scan, and plain-audio trim transport; 508 automated tests; owner-confirmed native smoke (2026-10-16)
- `complete/030A_COMPLETED_VisualTransforms.md` — Core-owned base-visual transforms with software/Direct3D11 parity, optional whole-composition processing, Player validation flyouts, 438-test automated baseline, and user-confirmed manual smoke on both renderer paths (2026-08-17)
- `complete/029C_COMPLETED_EventDrivenWpfAutoHide.md` — event-driven WPF surface AutoHide (default on, `--no-auto-hide` opt-out, internal-host-only visibility) with additive Core `HasVisualContent`; Release 0 warnings/errors, 406 tests, format/Core/WPF/parity gates, Core+WPF packs, WPF specialist and final diff review no blockers, manual Player AutoHide smoke passed (2026-08-08)
- `complete/029B_COMPLETED_LoadResetPitchTempoOptions.md` — per-load pitch/tempo reset options with `MediaChangeReason.LoadReset`; Release 0 warnings/errors, 397 tests, format/Core/WPF/parity/package gates passed, strict review no blockers, manual reset smoke passed with both startup flags (2026-08-08)
- `complete/029A_COMPLETED_SessionEventsFoundation.md` — host-facing session event surface with public ordering/throttling contracts; Release 0 warnings/errors, 389 tests, format/Core/WPF/parity/package gates passed; Player host smoke and `--software` runtime smoke passed (2026-08-08)
- `complete/024A_COMPLETED_SoftwareRenderer.md` — portable deterministic in-memory BGRA composition with exact-pixel visual-regression coverage; Release Direct3D11 NativeHwndOverlay smoke confirmed for video/CDG play, seek, stop, and replay (2026-08-07)
- `complete/028A_COMPLETED_LightweightWpfSurfaces.md` — self-contained background/karaoke surfaces with selectable native-HWND or WPF-composited presentation (2026-08-07)
- `complete/025B_COMPLETED_RendererConfigurationOwnership.md` — Direct3D11 owns renderer-specific presentation options and DXGI present policy; portable Core contains no concrete renderer selection (2026-08-03)
- `complete/025A_COMPLETED_PresentationSurfaceContractNeutrality.md` — extensible borrowed native-surface payloads replace the closed HWND enum/handle contract; Core is surface-platform-neutral and Direct3D11 owns Win32 HWND extraction (2026-07-18)
- `complete/023A_COMPLETED_HostNativeDistributionPolicy.md` — host-owned BASS/FFmpeg source, licence, version, RID, package-layout, update, and clean-machine publish-smoke policy (2026-07-17)
- `complete/021A_COMPLETED_CrossPlatformRendererAbstraction.md` — Core owns native-frame contracts, hardware-decode policy seams, presentation coordination, composition planning, and public renderer contracts; Windows and Direct3D11 are independently selectable packages with no production friend access (2026-07-17)
- `complete/016B_COMPLETED_MediaLoopPacerDeadlineManagement.md` — Core owns media-loop deadline accumulation, work-time compensation, lateness resynchronization, and optional late yield through MediaLoopPacer; platform packages only supply IPrecisionWaiter native wait primitives (2026-07-17)
- `complete/012_COMPLETED_CoreWindowsPlatformBoundary.md` — Core owns DI composition; Windows supplies platform services through explicit contracts; VideoPipeline moved into Core; InternalsVisibleTo for Windows removed; core boundary script enforces this (2026-07-17)
- `complete/011A_COMPLETED_SeamlessPrimaryVideoLooping.md` — video-only loop pre-seek and virtual-clock commit without a reactive decoder restart (2026-07-11)
- `complete/013B_COMPLETED_PlaybackSyncControls.md` — per-item A/V, CDG, and subtitle synchronization controls with 100ms operator adjustments (2026-07-11)
- `complete/013A_COMPLETED_GeneralSubtitleEngineBeyondCDG.md` — Core-owned sidecar and embedded text subtitle engine with track selection and configurable text styling (2026-07-11)
- `complete/016A_COMPLETED_ClockAudioEngineAbstractionSplit.md` — clock source abstraction split; no-audio timing and tempo no longer use the audio abstraction (2026-07-10)
- **007B** — hardware frame release safety (2026-07-07)
- **007A** — present outside transport-visible locks (2026-07-07)
- **006B** — audio device hotplug (2026-07-07)
- **006A** — audio latency feedback (2026-07-07)
- **005C** — decoder device lost recovery (2026-07-06)
- **005B** — device lost recovery (2026-07-06)
- **005A** — device lost detection (2026-07-06)
- **004B** — release diagnostics (2026-07-06)
- **004A** — error surface (2026-07-06)
- 001–003E, 008A, 009A — git history only

## Future Ideas

These are worthwhile directions, not problem-gated work. An idea becomes active when it
is explicitly selected and moved into `active/` with bounded acceptance criteria.

- `future/032A_IDEA_AudioCdAndCdgDiscPlayback.md` - audio CD and CD+G disc playback through
  BASSCD, feeding disc subchannel graphics into the existing CDG engine (owner decision,
  2026-10-17).

Next stage: 032A (needs CD drive measurements first).

Stages 029C-031B plus the revised 014A are the agreed feature sequence after Stage 029B:
auto-hide polish (029C, completed), visual transforms (030A, completed), analysis and
trimming, prepared-next and audio crossfade, then streaming.

All previously listed ideas (through 022A) were moved to `active/` on 2026-10-16. Their
deferred later slices are recorded in each active stage file.

