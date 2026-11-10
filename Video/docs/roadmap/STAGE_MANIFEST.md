# Stage Manifest

## Active

Stages with a slice pending the owner's smoke or planned slices still to do. A stage moves to
`complete/` once its slices have passed; its deferred follow-ups become future ideas.

- `active/026B_ACTIVE_CrossPlatformPlatformServices.md` - Linux baseline implemented
  (`updates/026B_1`); native FFmpeg + Core decode validated on Linux; Linux smoke with BASS
  passed 2026-11-05 in the sandbox (no `AudioDeviceChanged` on Linux: the sound server reroutes;
  ENG-040); Windows sidecar check (step 6) pending; macOS deferred.
- `active/026A_ACTIVE_CrossPlatformRendererBackends.md` - Vulkan slices 1-2 implemented,
  pending manual smoke (`updates/026A_1`, `updates/026A_2`: SPIR-V wash + CDG overlay);
  validated on lavapipe; swapchains/subtitles/transforms/OpenGL/Metal later.
- `active/027A_ACTIVE_UiAdapterImplementations.md` - Avalonia and WinForms slices
  implemented (`updates/027A_1`, `updates/027A_3`). Slice 2, the Avalonia GPU-shared
  surface, is implemented and pending manual smoke (`updates/027A_2`): Direct3D11 keyed-mutex
  shared textures and Vulkan opaque-fd images go into Avalonia's compositor, with a frame-sink
  fallback. Slice 5, Avalonia second-screen output (one player on two screens; renderers
  accept secondary frame-sink and shared-image surfaces; Audio mode clears the surface), is
  implemented and pending manual smoke (`updates/027A_5`). WinUI 3 is deferred.
- `active/037A_ACTIVE_FatzAudioEngine.md` - FatzAudio behind `IAudioEngine` (opt-in, BASS stays
  the default), with CD+G and MIDI lyrics drawn by FatzAudio's surfaces (owner decision
  2026-11-01). Slice 0 (verification spike and karaoke measurements) done on Linux; its Windows console-app smoke
  is superseded by the slice 2 owner smoke (the real engine in the players; closed 2026-11-05). Slice 1 (seams for MIDI
  events, audio levels, fades, EQ and trim, so internal code needs no BASS handle; no behaviour
  change) merged, owner smoke passed 2026-11-02 (`updates/037A_1`). FatzAudio 1.0.0-rc.1 answers
  our blocking surface requests and silence detection (reply:
  `docs/fatzaudio/2026-11-03_MediaControl_Reply_Rc1.md`). Slice 2 (opt-in `FatzAudioEngine` for
  files and archives in the new `FatzMedia.Video.Audio` package, pinned to
  1.0.0-rc.1; `--audio=fatz`) merged, owner smoke passed 2026-11-03 (`updates/037A_2`). Loudness
  (LUFS) normalization on the FatzAudio engine (`MediaPlayerConfiguration.Normalization`, -14 LUFS
  default, per-user scan cache) implemented, pending manual smoke (`updates/037A_Normalization`). Slice 3
  (classic CD+G drawn by FatzAudio with the FatzAudio engine) implemented, pending manual smoke
  (`updates/037A_3`). Slice 4 (MIDI, trackers, network: `updates/037A_4`) and slice 5 (video audio on
  FatzAudio's clock; a FatzAudio player needs no BASS: `updates/037A_5`) implemented, pending manual
  smoke. Analysis:
  `docs/fatzaudio/2026-11-01_MediaControl_Analysis_KaraokeHandOff.md`.
- `active/041A_ACTIVE_EngineInternalFollowUps.md` - small engine follow-ups left by completed
  stages 010A, 015A, 017A, 019A and 022A; slice 1 extracts the video loop-boundary rules
  (`VideoLoopBoundary`), slice 2 the adaptive presentation-lead rules
  (`PresentationLeadController`), slice 3 the post-seek recovery state machine
  (`PostSeekRecovery`); owner smoke passed 2026-10-18 (`updates/041A_1` to `updates/041A_3`).
  The 010A/019A, 017A and 022A items are not started.
- `active/042A_ACTIVE_HdrFollowUps.md` - HDR follow-ups from 038A. Slice 1 (HLG tone mapping:
  inverse OETF, BT.2100 OOTF at 1000 nits, BT.2390 curve; CPU tables and the Direct3D11 shader)
  is implemented, pending manual smoke (`updates/042A_1`). Slice 2 (HDR output on HDR displays,
  Direct3D11, opt-in `Direct3D11PresentationOptions.HdrOutput`: FP16 scRGB swap chain for the
  primary window, SDR layers at the system SDR white level, SDR fallback) is implemented,
  pending manual smoke (`updates/042A_2`); the WPF Player turns it on with `--hdr-output`. Owner
  2026-11-06: no HDR display now or planned, so slice 2's HDR-display check cannot run here; its SDR
  fallback passed (backlog test 4). Treat HDR output as untested on real HDR hardware.
  Secondary windows, offscreen surfaces and the Avalonia GPU-shared surface stay SDR.
- `active/043A_ACTIVE_PlayerModes.md` - player modes (Full, Background, Audio); slice 1 (mode at
  creation, light loading, `AutoBackground`, `TransparentBackground`, `FromColor`) and slice 2
  (`SetModeAsync`, switching on the fly, `ModeChanged`) implemented, pending manual smoke
  (`updates/043A_1`, `updates/043A_2`).

- `active/044A_ACTIVE_TestPlayerParity.md` - the WPF, Avalonia and WinForms test players on one
  shared view model (`FatzMedia.Video.Sample.Player.Shared`), at feature parity with the WPF
  player, with a shared command line; slice 1 owner smoke passed 2026-10-26 (`updates/044A_1`).

Engineering audit follow-ups are tracked in `ENGINEERING_ISSUES.md`.

## Reverted

- original S3 shared cursor (2026-07-09) — reverted; superseded by revised S3 with drain-audio-then-seek

## Completed

- `complete/051A_COMPLETED_FirstFrameReadySignal.md` — `MediaSurface.FirstFrameReady` on WPF, Avalonia and WinForms (once per load, UI thread; WPF composited in the bitmap-writing operation) and the A/B swap pattern (TriviaBomb); WPF `CompositedUiThreadTime` (047A's before numbers); the WPF player's `--ab-demo`; the test players' 500 ms startup delay removed; owner smoke passed 2026-11-06/07 (`updates/051A_1`, `scripts/smoke-051A.ps1`)
- `complete/045A_COMPLETED_GpuDecodingEveryPath.md` — GPU decoding on every presentation path (full compatibility): D3D11VA on the Avalonia surface, Vulkan Video on the Vulkan renderer's device (Linux and Windows) and the Avalonia shared GPU image for Vulkan frames on Windows; owner smoke passed 2026-10-26/27 on Windows (`updates/045A_1` to `045A_3`); Linux drivers unverified (ENG-038)
- `complete/046A_COMPLETED_AdapterParity.md` — adapter and player parity (no feature solo for one player): shared Core auto-hide, presentation path and lightweight player controller; `SecondaryPresentationWindow`, `KaraokeSurface` and `BackgroundSurface` in WPF, Avalonia and WinForms; WinForms frame sink and overlay; Player.Shared chrome, stats, screen rule and renderer request; GPU-first `--renderer=auto`; the player-count and renderer toolbar in every player; owner smoke passed 2026-10-26/27 (`updates/046A_1` to `046A_3`)
- `complete/039A_COMPLETED_NetworkVideoPlayback.md` — network video through FFmpeg (HTTP/HTTPS progressive, HLS VOD and live, certificate checks), a forward read-ahead with one download for picture and sound, and reconnect after a dropped network; owner smoke passed 2026-10-25/26; follow-ups in ENG-034 (completed 2026-10-26). Follow-up update `updates/039A_3` (https and HLS audio through FFmpeg so certificates are verified, ENG-034 triage: clock held for picture-only outages, bounded reconnects, moov-at-end message): implemented, pending manual smoke
- `complete/038A_COMPLETED_HdrVideoToneMapping.md` — HDR10 to SDR tone mapping (BT.2390, SDR white 203 nits) on the CPU for every renderer and in a Direct3D11 shader for hardware-decoded video, with CPU fallback; owner smoke passed 2026-10-18; HLG and HDR output in 042A (completed 2026-10-18)
- `complete/010A_COMPLETED_SoftwareVideoUploadCopyPath.md` — copy map, repeatable software-video benchmark, per-composition snapshot copy removed; owner smoke passed 2026-10-17; follow-ups in 041A (completed 2026-10-18)
- `complete/014A_COMPLETED_StreamingNetworkPlayback.md` — source capability matrix and HTTP/HTTPS audio with retries; owner smoke passed 2026-10-17; network video deferred to 039A (completed 2026-10-18)
- `complete/015A_COMPLETED_BroadVideoPipelineSplit.md` — responsibility map, seek policy and the shared seek pump extracted; owner smoke passed 2026-10-17; remaining seams in 041A (completed 2026-10-18)
- `complete/017A_COMPLETED_DisplayRateCompositorRewrite.md` — cadence model and report, per-monitor refresh rate, opt-in display-locked timing with per-display cadence; owner smoke passed 2026-10-18; later work in 041A (completed 2026-10-18)
- `complete/018A_COMPLETED_CDGSnapshotOwnershipRedesignPooling.md` — CDG snapshot lifetime map, counters and immutable snapshot reuse; owner CDG smoke passed 2026-10-17 (completed 2026-10-18)
- `complete/019A_COMPLETED_VideoFrameEventArgsReuseApiRedesign.md` — video frame event lifetime matrix, written ephemeral contract and conformance tests; no behaviour change (completed 2026-10-18)
- `complete/020A_COMPLETED_AudioFilterFramework.md` — audio filter contract and bypassable parametric equalizer; owner smoke passed 2026-10-17 (completed 2026-10-18)
- `complete/022A_COMPLETED_ThreadModelProfilingAndSimplification.md` — thread map and capture tooling, per-frame costs removed, reused scheduler threads, CD+G seek checkpoints; owner smoke passed 2026-10-18 (completed 2026-10-18)
- `complete/033A_COMPLETED_FfmpegAudioParityAndFallback.md` — FFmpeg audio parity for video soundtracks (equalizer, fades, normalization) and the FFmpeg fallback for audio formats no BASS plug-in decodes; owner smoke passed 2026-10-17 and 2026-11-06 (real `.ape` and `.dsf`) (completed 2026-11-06)
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

- `future/048A_IDEA_FatzMediaVideoRename.md` - rename the engine to `FatzMedia.Video` in the
  FatzMedia family (owner 2026-11-03); its own stage, after the 037A decision.
- `future/049A_IDEA_PresentTimeFeedback.md` - **planned** (owner 2026-11-03): correct the
  present-time estimate with Direct3D11 present statistics, after 037A slice 3.
- `future/050A_IDEA_TagsAndFileInformation.md` - **planned** (owner 2026-11-05): `player.Tags` and
  `MediaInfo.ReadAsync` (tags, details, hashes, cover-art thumbnails); after FatzAudio rc.4 and 056A.
- `future/052A_IDEA_ImageOverlayAndPictureFade.md` - **planned**: a host image overlay layer on every
  renderer, "change together" with a load, and `FadePictureAsync`.
- `future/053A_IDEA_StreamMediaSource.md` - **planned**: `MediaInputSource.FromStream` through FFmpeg
  custom I/O.
- `future/054A_IDEA_FullIdleCostCheck.md` - **planned**: measure and fix idle-feature costs in `Full`
  (no `Simple` mode, owner).
- `future/055A_IDEA_ReconfigurePlayerInPlace.md` - **planned**: `ReconfigureAsync` keeps media,
  position, events and surfaces.
- `future/056A_IDEA_FatzAudioArchiveReader.md` - **planned**: archives through FatzAudio's reader,
  SharpCompress and rar dropped; after FatzAudio's archive API.
- `future/059A_IDEA_Arm64Targets.md` - **planned** (owner 2026-11-06): win-arm64 and linux-arm64 builds,
  natives (FatzAudio is adding them; FFmpeg via 058A) and an ARM smoke.
- `future/058A_IDEA_ShipFfmpegWithTheControl.md` - **planned** (owner 2026-11-06): the LGPL FFmpeg
  libraries ship in runtime packages (win-x64, linux-x64) with the licence, notices and source; no GPL
  or nonfree build; replaces the host-owned FFmpeg policy (023A).
- `future/057A_IDEA_UiFriendlyAsyncApi.md` - **planned** (owner 2026-11-05, test-player audit):
  `BeginScrubAsync`/`EndScrubAsync`, `DisposeAsync`, and the players use Core's events instead of polling.
- `future/047A_IDEA_WpfSharedGpuSurface.md` - WPF composited mode from a shared GPU image
  (`D3DImage`) instead of a CPU copy: **planned, next (queue item 2; 051A completed 2026-11-07)** (owner 2026-11-05; UI-thread
  time per frame matters for hosts animating over video, and shows run on laptops). Guards for R1, R5
  and R7 decided in the idea.
- `future/040A_IDEA_OverlappingCrossfade.md` - two songs audible at once during a crossfade,
  deferred from 031B.

- `future/032A_IDEA_AudioCdAndCdgDiscPlayback.md` - audio CD and CD+G disc playback through
  BASSCD, feeding disc subchannel graphics into the existing CDG engine (owner decision,
  2026-10-17).

Next stage: 032A (needs CD drive measurements first).

Stages 029C-031B plus the revised 014A are the agreed feature sequence after Stage 029B:
auto-hide polish (029C, completed), visual transforms (030A, completed), analysis and
trimming, prepared-next and audio crossfade, then streaming.

All previously listed ideas (through 022A) were moved to `active/` on 2026-10-16. Their
deferred later slices are recorded in each active stage file.

