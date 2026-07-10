# Stage Manifest

## Active / Backlog

*(none — next stage to be planned)*

## Reverted

- original S3 shared cursor (2026-07-09) — reverted; superseded by revised S3 with drain-audio-then-seek

## Completed

- **012A.S3** — shared cursor + unified seek, drain-audio-then-seek gapless loop (2026-07-10)
  - Design: `012A_S3_DESIGN_SharedCursorUnifiedSeek.md` | Notes: `updates/012A_S3/StageUpdate.md`
- **012A.S2** — MediaDemuxer seam behind VideoAudioEngine (2026-07-09)
- **012A.S1** — MediaDemuxer seam behind VideoDecoder (2026-07-09)
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

## Future

- `010A_FUTURE_SoftwareVideoUploadCopyPath.md`
- `011A_FUTURE_SeamlessPrimaryVideoLooping.md`
- `013A_FUTURE_GeneralSubtitleEngineBeyondCDG.md`
- `014A_FUTURE_StreamingNetworkPlayback.md`
- `015A_FUTURE_BroadVideoPipelineSplit.md`
- `016A_FUTURE_ClockAudioEngineAbstractionSplit.md`
- `017A_FUTURE_DisplayRateCompositorRewrite.md`
- `018A_FUTURE_CDGSnapshotOwnershipRedesignPooling.md`
- `019A_FUTURE_VideoFrameEventArgsReuseApiRedesign.md`
- `020A_FUTURE_FullAudioFilterFramework.md`
- `021A_FUTURE_CrossPlatformRendererAbstraction.md`
- `022A_FUTURE_ThreadModelProfilingReductionGate.md`
