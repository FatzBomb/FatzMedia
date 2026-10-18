# Stage Manifest

## Active

Stages with a slice pending the owner's smoke or planned slices still to do. A stage moves to
`complete/` once its slices have passed; its deferred follow-ups become future ideas.

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
- `active/033A_ACTIVE_FfmpegAudioParityAndFallback.md` - slice 1 (equalizer, fades,
  normalization for video audio) implemented; owner smoke passed 2026-10-17 (quiet-opening
  film, no burst). Slice 2 (FFmpeg fallback for unclaimed audio formats) implemented,
  pending manual smoke: the owner has no such files yet (`updates/033A_1`,
  `updates/033A_2`).

- `active/041A_ACTIVE_EngineInternalFollowUps.md` - small engine follow-ups left by completed
  stages 010A, 015A, 017A, 019A and 022A; slice 1 extracts the video loop-boundary rules
  (`VideoLoopBoundary`), slice 2 the adaptive presentation-lead rules
  (`PresentationLeadController`), slice 3 the post-seek recovery state machine
  (`PostSeekRecovery`); owner smoke passed 2026-10-18 (`updates/041A_1` to `updates/041A_3`).
  The 010A/019A, 017A and 022A items are not started.

Engineering audit follow-ups are tracked in `ENGINEERING_ISSUES.md`.

## Reverted

- original S3 shared cursor (2026-07-09) — reverted; superseded by revised S3 with drain-audio-then-seek

## Completed

- `complete/038A_COMPLETED_HdrVideoToneMapping.md` — HDR10 to SDR tone mapping (BT.2390, SDR white 203 nits) on the CPU for every renderer and in a Direct3D11 shader for hardware-decoded video, with CPU fallback; owner smoke passed 2026-10-18; HLG and HDR output in 042A (completed 2026-10-18)
- `complete/010A_COMPLETED_SoftwareVideoUploadCopyPath.md` — copy map, repeatable software-video benchmark, per-composition snapshot copy removed; owner smoke passed 2026-10-17; follow-ups in 041A (completed 2026-10-18)
- `complete/014A_COMPLETED_StreamingNetworkPlayback.md` — source capability matrix and HTTP/HTTPS audio with retries; owner smoke passed 2026-10-17; network video deferred to 039A (completed 2026-10-18)
- `complete/015A_COMPLETED_BroadVideoPipelineSplit.md` — responsibility map, seek policy and the shared seek pump extracted; owner smoke passed 2026-10-17; remaining seams in 041A (completed 2026-10-18)
- `complete/017A_COMPLETED_DisplayRateCompositorRewrite.md` — cadence model and report, per-monitor refresh rate, opt-in display-locked timing with per-display cadence; owner smoke passed 2026-10-18; later work in 041A (completed 2026-10-18)
- `complete/018A_COMPLETED_CDGSnapshotOwnershipRedesignPooling.md` — CDG snapshot lifetime map, counters and immutable snapshot reuse; owner CDG smoke passed 2026-10-17 (completed 2026-10-18)
- `complete/019A_COMPLETED_VideoFrameEventArgsReuseApiRedesign.md` — video frame event lifetime matrix, written ephemeral contract and conformance tests; no behaviour change (completed 2026-10-18)
- `complete/020A_COMPLETED_AudioFilterFramework.md` — audio filter contract and bypassable parametric equalizer; owner smoke passed 2026-10-17 (completed 2026-10-18)
- `complete/022A_COMPLETED_ThreadModelProfilingAndSimplification.md` — thread map and capture tooling, per-frame costs removed, reused scheduler threads, CD+G seek checkpoints; owner smoke passed 2026-10-18 (completed 2026-10-18)
- `complete/031A_COMPLETED_PreparedNextPlaylistSlot.md` — depth-one prepared-next slot with Player Next panel; owner smoke passed 2026-10-17 (completed 2026-10-18)
- `complete/031B_COMPLETED_AudioFadeCrossfadeModel.md` — fade envelopes, single-handle fades and fade-through transitions; owner smoke passed 2026-10-17; overlapping crossfade deferred to 040A (completed 2026-10-18)
- `complete/034A_COMPLETED_NativeDependencyReport.md` — native dependency report; owner smoke passed 2026-10-17 (completed 2026-10-18)
- `complete/035A_COMPLETED_ControlOwnedSeekScrubbing.md` — control-owned seek scrubbing and optional silent drag; owner smoke passed 2026-10-17/18 (completed 2026-10-18)
- `complete/036A_COMPLETED_RendererFallbackChain.md` — renderer fallback chain (GPU, WARP, software) with Player `--renderer=`; owner smoke passed 2026-10-18 (completed 2026-10-18)
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

- `future/039A_IDEA_NetworkVideoPlayback.md` - HTTP/HTTPS video, deferred from 014A.
- `future/040A_IDEA_OverlappingCrossfade.md` - two songs audible at once during a crossfade,
  deferred from 031B.
- `future/042A_IDEA_HdrFollowUps.md` - HLG tone mapping and real HDR output on HDR displays,
  deferred from 038A.

- `future/032A_IDEA_AudioCdAndCdgDiscPlayback.md` - audio CD and CD+G disc playback through
  BASSCD, feeding disc subchannel graphics into the existing CDG engine (owner decision,
  2026-10-17).

Next stage: 032A (needs CD drive measurements first).

Stages 029C-031B plus the revised 014A are the agreed feature sequence after Stage 029B:
auto-hide polish (029C, completed), visual transforms (030A, completed), analysis and
trimming, prepared-next and audio crossfade, then streaming.

All previously listed ideas (through 022A) were moved to `active/` on 2026-10-16. Their
deferred later slices are recorded in each active stage file.

