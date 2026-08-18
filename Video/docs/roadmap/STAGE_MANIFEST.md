# Stage Manifest

## Active

- None. Select and bound the next stage before implementation begins.

Engineering audit follow-ups are tracked in `ENGINEERING_ISSUES.md`.

## Reverted

- original S3 shared cursor (2026-07-09) — reverted; superseded by revised S3 with drain-audio-then-seek

## Completed

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

Recommended next: `future/030B_IDEA_SilenceTrimmingAnalysisCache.md` - audio silence trimming with a versioned media-analysis cache.

Stages 029C-031B plus the revised 014A are the agreed feature sequence after Stage 029B:
auto-hide polish (029C, completed), visual transforms (030A, completed), analysis and
trimming, prepared-next and audio crossfade, then streaming.

The list is ordered by recommended next work, cross-platform enablement, product
features, then internal engine improvements.

- `future/030B_IDEA_SilenceTrimmingAnalysisCache.md` - audio silence trimming with a versioned media-analysis cache.
- `future/031A_IDEA_PreparedNextPlaylistSlot.md` - host-owned playlist with a Core depth-one prepared-next slot.
- `future/031B_IDEA_AudioFadeCrossfadeModel.md` - audio fade/crossfade envelope model for ordinary local audio.
- `future/014A_IDEA_StreamingNetworkPlayback.md` - direct HTTP/HTTPS audio/video with a source capability matrix.
- `future/026A_IDEA_CrossPlatformRendererBackends.md` - Vulkan, OpenGL, and Metal renderer packages.
- `future/026B_IDEA_CrossPlatformPlatformServices.md` - Linux and macOS platform-service packages.
- `future/027A_IDEA_UiAdapterImplementations.md` - WinUI 3, WinForms, and Avalonia adapters.
- `future/020A_IDEA_AudioFilterFramework.md` - ordered, bypassable audio processing.
- `future/017A_IDEA_DisplayRateCompositorRewrite.md` - display-rate scheduling and frame-repeat policy.
- `future/015A_IDEA_BroadVideoPipelineSplit.md` - incremental responsibility extraction.
- `future/010A_IDEA_SoftwareVideoUploadCopyPath.md` - reduce software-frame copy and upload cost.
- `future/018A_IDEA_CDGSnapshotOwnershipRedesignPooling.md` - explicit snapshot ownership and reuse.
- `future/019A_IDEA_VideoFrameEventArgsReuseApiRedesign.md` - explicit event payload lifetimes.
- `future/022A_IDEA_ThreadModelProfilingAndSimplification.md` - measured thread ownership cleanup.
