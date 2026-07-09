<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: DONE — implemented, automated gates pass, manual on-device smoke clean (2026-07-09). See docs/roadmap/updates/012A_S2/StageUpdate.md.

# 012A.S2 — Route `VideoAudioEngine` through its own `MediaDemuxer`

Parent: `docs/roadmap/012A_FUTURE_SharedDemuxerInputLayer.md`
Plan: `docs/roadmap/012A.2_PLAN_SharedDemuxerImplementation.md` (sub-stage S2)
Mapping: `docs/roadmap/012A_NOTE_SharedDemuxerInputLayerMap.md`
Prior: `012A_S1_BACKLOG_MediaDemuxerBehindVideoDecoder.md` (DONE)

## Status

Backlog. Second source-editing sub-stage. Still behavior-preserving and still
**two separate demuxer instances** (two format contexts, two cursors) — this
proves the `MediaDemuxer` abstraction fits audio *without* sharing a cursor yet.

## Goal

Replace `VideoAudioEngine`'s inline `AVFormatContext` ownership with delegation
to its own `MediaDemuxer` instance, mirroring what S1 did for `VideoDecoder`.
Output must be byte-identical to today. No shared cursor; no seek/loop change.

## Scope (in)

- `FatzBombMedia.MediaControl/Core/Audio/VideoAudioEngine.cs`: replace the
  `_formatContext` field and its inline open/read/seek/close calls with a
  `MediaDemuxer` instance. Specifically:
  - `Open(string)` (`:247-291`): `avformat_open_input` + `find_stream_info` →
    `_demuxer.Open`; `av_find_best_stream(AUDIO)` → `_demuxer.FindBestStream`;
    `_formatContext->streams[...]` → `_demuxer.GetStream`;
    `_formatContext->duration` → `_demuxer.FormatDurationTicks`.
  - `DecodeAndPushAudioCore` (`:320-369`): `av_read_frame(_formatContext, packet)`
    → `_demuxer.ReadPacket(packet)`.
  - `SeekDecoder` (`:826-840`): `av_seek_frame(_formatContext, ...)` →
    `_demuxer.Seek(...)`; the `_formatContext == null` guard → `!_demuxer.IsOpen`.
  - `CloseCore` (`:521-594`): `avformat_close_input(&_formatContext)` →
    `_demuxer.Close()`.
  - `IsOpenCore` (`:194`): `_formatContext != null` → `_demuxer.IsOpen`.
  - `Dispose`: dispose the demuxer.

## Scope (out) — do NOT do in S2

- Do not introduce a shared cursor or packet fan-out (that is S3).
- Do not change seek/loop/discard **semantics** — `_discardAudioBeforeSeconds`,
  `PrebufferAudio`, throttle watermarks (4.0s/2.0s), `WrapDecoderForLoop`, and
  `LoopPlayback`/`CompletedLoopCount` must behave exactly as before.
- Do not change `VideoAudioEngine`'s public/internal surface used by
  `VideoPipeline` (`Open`, `DecodeAndPushAudio`, `Play`, `Pause`, `Stop`,
  `Seek`, `PrebufferAudio`, `Close`, `Dispose`, properties, events).
- Do not touch `VideoDecoder` (S1 already did) or BASS push/tempo topology.
- No public API changes; no WPF adapter changes (Core-only).

## Must Not Break

- Pushed audio frame count + end-position sequence (byte-identical to baseline).
- BASS tempo/pitch path (`CreateBassStreams`, `SetTempo`/`SetPitch`).
- Audio positions (`Position`, `TryGetPositionSnapshot`) and output-latency
  correction (006A).
- Throttle timing (2.0s/4.0s watermarks) and continuous-timeline loop bookkeeping
  (`_lastPushedAudioEndSeconds`, `AudioLoopBoundaryTracker`).
- Gapless audio-backed loop (`WrapDecoderForLoop` seeks the audio decoder back to
  0 without stopping the BASS channel — must survive the extraction).
- A/V sync, seek (preview+settle), CDG-paired playback, media switch.

## Implementation notes

- `VideoAudioEngine` allocates a **fresh** `AVPacket` per `DecodeAndPushAudioCore`
  call (`:339`) — keep that; `MediaDemuxer.ReadPacket` takes a caller-provided
  packet, so the per-call alloc/free pattern is unchanged.
- Non-audio packets are skipped via `stream_index` check (`:347`), **not** via
  stream-level discard. Do not add a discard loop here — audio's read loop
  intentionally still sees and throws away video packets (mapping §2). S3 is
  where that changes.
- Keep the smallest edit: move the format-context field and its call sites, not a
  rewrite of decode/push/throttle flow.

## Validation (risk level: renderer/timing/media/native lifetime)

1. `dotnet build FatzBombMedia.sln` (Debug) — clean.
2. `dotnet build -c Release FatzBombMedia.sln` — **0 warnings** (warnings-as-errors).
3. `scripts/check-wpf-boundary.ps1` and `scripts/check-presentation-parity.ps1` — pass.
4. `dotnet test -c Release` — no regressions.
5. Manual smoke: A/V sync, tempo ±20%, seek (preview+settle), gapless loop,
   CDG-paired, audio-only regression, media switch. Audio frame-count /
   end-position and loop-wrap sequences must match baseline.

## Stop conditions

- Any audio position / throttle / loop-wrap divergence → revert, do not proceed to S3.
- If the extraction forces a `VideoAudioEngine` signature change → stop and re-scope.
- If gapless audio looping regresses (audio gap at boundary) → revert.

## Definition of done

- `VideoAudioEngine` no longer owns an `AVFormatContext*` directly; it delegates
  to a `MediaDemuxer` instance.
- Public/internal surface unchanged; all callers compile untouched.
- Debug + Release clean, scripts pass, tests pass, manual smoke matches baseline.
- Update note written under `docs/roadmap/updates/012A_S2/`.
- **Stable landing point:** after S2 the codebase is functionally identical to
  today but both engines sit on the same demuxer *type*. 012A.2 may pause here
  indefinitely before the riskier S3 (shared cursor).
- Next: promote S3 only after this lands and is confirmed on-device.