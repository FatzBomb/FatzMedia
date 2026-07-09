<!-- Design note for stage 012A. Pure mapping. No behavior change. Stage stays FUTURE. -->

# 012A — Shared Demuxer / Input Layer Map

Stage file: `docs/roadmap/012A_FUTURE_SharedDemuxerInputLayer.md`
Executes the stage's *First Safe Step*: "Mapping only: document `VideoDecoder`,
`VideoAudioEngine`, open/seek, packet ownership, audio discard/prebuffer, and
stream lifetime before source edits."

This note is analysis only. **No code was changed. The stage remains FUTURE**
and is not promoted; the manifest is untouched. This is the pre-work that must
exist before any single-demux source edit is even proposed.

## 0. Scope

The stage targets the fact that a video-with-audio container is **demuxed
twice** — once for video, once for audio — via two independent
`AVFormatContext`s on the same file. This note maps exactly where that happens
and every behavior a future single-demux layer must preserve. It does **not**
propose an implementation.

## 1. The double-demux today (confirmed)

`VideoPipeline.Open(path)` (`FatzBombMedia.MediaControl/Pipeline/VideoPipeline.cs:198`)
opens the same source **twice**:

```
VideoPipeline.Open(path)
  → _videoDecoder = VideoDecoder.FromConfiguration(...)      (VideoPipeline.cs:206)
      → VideoDecoder.Open(path)                              (VideoDecoder.cs:103)
          → avformat_open_input(&_fmt, path)                 (VideoDecoder.cs:109)
          → avformat_find_stream_info(_fmt)                  (VideoDecoder.cs:112)
          → av_find_best_stream(VIDEO)                       (VideoDecoder.cs:118)
          → discard ALL non-video streams (AVDISCARD_ALL)    (VideoDecoder.cs:128-132)
  → _audioEngine = new VideoAudioEngine(...)                 (VideoPipeline.cs:213)
      → VideoAudioEngine.Open(path)                          (VideoAudioEngine.cs:247)
          → avformat_open_input(&_formatContext, path)       (VideoAudioEngine.cs:259)
          → avformat_find_stream_info(_formatContext)        (VideoAudioEngine.cs:263)
          → av_find_best_stream(AUDIO)                        (VideoAudioEngine.cs:269)
```

Two `avformat_open_input` + `avformat_find_stream_info` + `av_find_best_stream`
probes run on one file. `VideoDecoder._fmt` (`VideoDecoder.cs:34`) and
`VideoAudioEngine._formatContext` (`VideoAudioEngine.cs:21`) are separate,
independently-owned format contexts. This is the structural duplication 012A
exists to remove.

### Who opens a container — full picture

| Consumer | Demux mechanism | File |
|---|---|---|
| Main video | Own `AVFormatContext` (`_fmt`), FFmpeg | `VideoDecoder.cs:34, 103` |
| Main video's audio | Own `AVFormatContext` (`_formatContext`), FFmpeg | `VideoAudioEngine.cs:21, 247` |
| Audio-only playback | **BASS pull stream** — BASS demuxes internally, no FFmpeg format context | `AudioStreamManager.OpenFromPath` (`AudioStreamManager.cs:81-109`, per 006A note) |
| CD+G | **Sidecar `.cdg`** — not a container demux; loaded by the CDG engine | `CdgPipeline.OpenStandalone` / `OpenPaired` (`CdgPipeline.cs:33-79`) |

Only the **video-with-audio** path double-demuxes an FFmpeg container. The
audio-only and CDG paths do not use FFmpeg demuxing at all, so a shared FFmpeg
demux layer would initially serve only the `VideoDecoder` + `VideoAudioEngine`
pair. Subtitles/multi-track/chapters (013A) and streaming (014A) are the future
consumers named in the stage rationale — they do not exist yet.

## 2. Packet read loops & ownership

Each engine runs its **own** `av_read_frame` loop against its **own** format
context, with its **own** `AVPacket`.

### Video

- `VideoDecoder.DecodeNextFrame` (`VideoDecoder.cs:157`) reads into the single
  reusable field `_packet` (`VideoDecoder.cs:42`, allocated in `Open` at
  `:140`, freed in `Close` at `:931-935`).
- Only video packets are processed (`_packet->stream_index == _videoIndex`,
  `:175`); every other packet never even arrives because non-video streams are
  marked `AVDISCARD_ALL` at open (`:128-132`), so `av_read_frame` returns video
  packets only.
- Ownership: `av_packet_unref(_packet)` after each read (`:182`). One packet
  object reused for the decoder's lifetime.

### Audio

- `VideoAudioEngine.DecodeAndPushAudioCore` (`VideoAudioEngine.cs:320`)
  allocates a **fresh** packet **per call** via `av_packet_alloc` (`:339`),
  reads in a loop, and frees it in `finally` (`av_packet_unref` + `av_packet_free`,
  `:366-367`).
- Non-audio packets are `av_packet_unref`'d and skipped (`:355`) — **not**
  discarded at the stream level (contrast with video). So the audio format
  context still reads and throws away video packets on every pass.
- Ownership: packet is local to each `DecodeAndPushAudioCore` invocation.

### Ownership summary

- **No shared packet pool.** Two format contexts, two packet objects, two read
  cursors advancing independently over the same file.
- The two read loops are driven from the **same decode thread**:
  `VideoPipeline.DecodeLoop` (`VideoPipeline.cs:1238`) calls `DecodeVideoBatch`
  (`:1446`, which calls `VideoDecoder.DecodeNextFrame`) then
  `_audioEngine.DecodeAndPushAudio` (`:1246`) each iteration. A shared demuxer
  would fan one `av_read_frame` result out to both consumers on this thread.

## 3. Open path

| Step | Video | Audio |
|---|---|---|
| Open container | `avformat_open_input` (`VideoDecoder.cs:109`) | `avformat_open_input` (`VideoAudioEngine.cs:259`) |
| Probe streams | `avformat_find_stream_info` (`:112`) | `avformat_find_stream_info` (`:263`) |
| Select stream | `av_find_best_stream(VIDEO)` (`:118`) | `av_find_best_stream(AUDIO)` (`:269`) |
| Codec open | `InitializeVideoDecoder` (`:134, 219`) — HW/SW selection | `InitializeDecoder` (`:278, 647`) — resampler + SwrContext |
| Duration | video-stream duration preferred over format duration (`:143-151`) | `_formatContext->duration` (`:284`) |
| Sink setup | frame queue only | `CreateBassStreams` — BASS push + tempo stream (`:278, 688`) |

Orchestration facts:

- `VideoPipeline.Open` derives `Duration = Math.Max(videoDuration, audioDuration)`
  (`VideoPipeline.cs:227`) — a shared demux must still expose per-stream duration
  for this.
- First frame is decoded synchronously under `_decodeLock` before returning
  (`DecodeUntilFrameAvailable`, `VideoPipeline.cs:240, 1468`), with a
  software-fallback path if the hardware decoder yields no startup frame
  (`TryFallbackToSoftwareVideo`, `:1485`).
- **Archive / byte sources are opened via temp files, independently per engine.**
  `VideoPipeline.OpenBytes` writes a temp file and calls `Open(path)`
  (`VideoPipeline.cs:251`); `VideoAudioEngine.Open(IArchiveEntryInfo)` writes
  its **own** temp file via `OpenBytes` → `CreateTempMediaPath`
  (`VideoAudioEngine.cs:293-312, 878`). So an archive entry can be materialized
  to disk **twice**. A shared demux should collapse this to one temp file (or a
  custom AVIO over the archive stream).

## 4. Seek path

`VideoPipeline.SeekCore` (`VideoPipeline.cs:556`) coordinates **two independent
container seeks** to the same target:

```
SeekCore(target)
  → _audioEngine.Seek(target, resumePlayback:false)          (VideoPipeline.cs:583)
      → VideoAudioEngine.SeekCore → SeekDecoder(target)        (VideoAudioEngine.cs:464, 826)
          → av_seek_frame(_formatContext, _audioStreamIndex)   (VideoAudioEngine.cs:832)
          → avcodec_flush_buffers                              (:835-836)
          → _discardAudioBeforeSeconds = target                (:838)
  → SeekVideoNearTarget(target, asPreview)                     (VideoPipeline.cs:596, 1821)
      → VideoDecoder.Seek(target, exact:)                      (VideoDecoder.cs:1034)
          → av_seek_frame(_fmt, _videoIndex, BACKWARD)          (VideoDecoder.cs:1042)
          → Flush(clearQueue)                                   (:1045)
          → _discardFramesBeforeSeconds = exact ? target : null (:1047)
```

Key facts a shared demux must preserve:

- **Two seek cursors, two discard-to-target gates.** Video uses
  `_discardFramesBeforeSeconds` (`VideoDecoder.cs:52`, applied in
  `ProcessVideoFrameFromDecoder` at `:651-659`); audio uses
  `_discardAudioBeforeSeconds` (`VideoAudioEngine.cs:35`, applied in
  `PushAudioFrame` at `:781-787`). A single read cursor would have to satisfy
  **both** discard windows from one seek.
- **Video is the flexible stream in SmartSeek.** Audio/CDG/clock move to the
  exact target first; video is allowed to stay frozen or present a nearby frame
  (`SeekVideoNearTarget` comment, `:1827-1838`; preview vs balanced budgets;
  `IsTinyVideoSeek` `:1870`). This asymmetry is deliberate and must survive.
- **Seek preview/settle pump** runs on a dedicated worker
  (`SeekWorkerLoop`, `:685`) with latest-wins semantics. A shared demux seek
  must remain non-blocking under this pump.
- **Loop wrap seeks are already divergent and must stay so:**
  - Video decoder proactively pre-seeks to 0 while tail frames are still queued
    (`TryPreSeekLoopStart`, `:1717`).
  - Audio wraps its **own** decoder at EOF and never stops its BASS channel
    (`VideoAudioEngine.WrapDecoderForLoop`, `:375`; `LoopPlayback` /
    `CompletedLoopCount`, `:63-75`).
  - `CommitAudioLoopWrap` (`:414`) catches the video side up with **no** audio
    seek/play calls. A shared demux must not force audio and video to share one
    seek at the loop boundary, or gapless audio looping breaks.

## 5. Audio discard / prebuffer

All in `VideoAudioEngine`:

- **Discard-to-target after seek:** `_discardAudioBeforeSeconds` set in
  `SeekDecoder` (`:838`), honored in `PushAudioFrame` (`:781-787`) — frames
  before the target are dropped, not pushed to BASS.
- **Prebuffer:** `PrebufferAudio(minimumFrames=8, maxDecodeAttempts=128)`
  (`:498`) decodes/pushes until a minimum number of frames land, used on
  post-seek resume (`VideoPipeline.cs:614`).
- **Throttle (push-stream backpressure):** `IsAudioBufferThrottledCore`
  (`:732`) with high/low watermarks `AudioBufferHighWatermarkSeconds = 4.0` /
  `AudioBufferLowWatermarkSeconds = 2.0` (`:14-15`). Buffered seconds =
  `_lastPushedAudioEndSeconds - GetContinuousPositionCore()`
  (`GetBufferedAudioSecondsCore`, `:762`).
- **Continuous end-mark:** `_lastPushedAudioEndSeconds` is monotonic across a
  loop wrap (`PushAudioFrame` guard, `:813-816`) so throttle math stays correct
  through the boundary.

Implication for 012A: the demuxer reads packets, but **audio-side throttling is
driven by how full the BASS push stream is**, not by how many packets are
available. A shared read loop must respect audio backpressure (stop pulling /
buffer packets) without starving video, and vice versa — the two consumers
drain at different rates.

## 6. Stream lifetime

- Both engines are created in `VideoPipeline.Open` (`:206, 213`) and disposed in
  `VideoPipeline.Close` (`:864`, `_audioEngine?.Dispose()` at `:891`,
  `_videoDecoder?.Dispose()` at `:897`). Format contexts are closed in each
  engine's own teardown: `VideoDecoder.Close` (`avformat_close_input`,
  `VideoDecoder.cs:937-941`) and `VideoAudioEngine.CloseCore`
  (`avformat_close_input`, `VideoAudioEngine.cs:569-575`).
- **The video decoder can be reopened independently of audio.**
  `SwapVideoDecoder` (`:1393`) disposes and recreates **only** `_videoDecoder`
  at the current position, leaving `_audioEngine` running. Used by:
  - startup HW→SW fallback (`TryFallbackToSoftwareVideo`, `:1485`);
  - device-lost recovery (`RunDecoderDeviceLostRecovery`, `:1292` → software
    fallback then hardware re-probe).
  This is a hard constraint: a shared demux **cannot** couple the audio stream's
  lifetime to a video-decoder rebuild.
- **Device-lost probe opens a throwaway container.**
  `HardwareDecodeDeviceAvailable` (`:1373`) opens a disposable `VideoDecoder`
  purely to test whether the D3D11 hardware-decode device is back — a third,
  transient demux of the same file. A shared demux must tolerate (or provide) a
  cheap capability probe that doesn't disturb the live read cursor.

## 7. Constraints a shared demux must preserve (from the stage "Must Not Break")

| Constraint | Where it lives today | Why a naive shared read breaks it |
|---|---|---|
| BASS tempo/pitch path | `VideoAudioEngine.CreateBassStreams` push+tempo (`:688-715`), `SetTempo`/`SetPitch` (`:605-625`) | Audio must keep its own PCM push cadence + throttle; a shared read must feed the push stream on demand, not lockstep with video. |
| Video-with-audio sync | `AudioPresentationClock` fed by `TryGetPositionSnapshot` (`VideoAudioEngine.cs:124`); `GetPresentationPosition` (`VideoPipeline.cs:1515`) | Position origin is the BASS audio clock, not packet position. Sync must not regress to "wherever the read cursor is." |
| Seek preview/settle | `SeekWorkerLoop` / `SeekCore` latest-wins (`:556, 685`); video-flexible SmartSeek | One shared seek must still satisfy audio's exact target **and** video's nearby-frame budget independently. |
| CDG/archive/sidecar | `CdgPipeline` sidecar (`CdgPipeline.cs`), per-engine temp files (`:878`) | CDG is not container-demuxed; archive entries are currently materialized per engine. Don't regress CDG; do dedupe the temp file. |
| Hardware decoder fallback | `SwapVideoDecoder` (`:1393`), `RunDecoderDeviceLostRecovery` (`:1292`) | Video decoder rebuild must not tear down the shared demux or the audio stream. |
| Gapless audio-backed loop | `VideoAudioEngine.LoopPlayback`/`WrapDecoderForLoop` (`:63, 375`), `CommitAudioLoopWrap` (`:414`) | Audio wraps its own decoder and never stops its channel; a forced shared seek at EOF would introduce an audio gap. |

## 8. Risks / open questions for promotion

These are the questions that must be answered **before** 012A moves off FUTURE.
No answers are proposed here.

1. **Read-cursor arbitration.** One `av_read_frame` cursor, two consumers that
   drain at different rates (video queue cap ~12–24 frames; audio 2–4 s push
   buffer). A shared demux needs per-stream packet fan-out queues with
   independent backpressure, or one consumer starves/overflows the other.
2. **Independent seek semantics.** Video-flexible SmartSeek vs audio-exact seek
   vs gapless-loop (audio wraps alone). A single container seek cursor must
   reconcile three different target/discard policies. This is the hardest part.
3. **Independent lifetime.** `SwapVideoDecoder` and device-lost recovery rebuild
   the video *decoder* while audio keeps playing. The shared *demuxer* must
   outlive a video-decoder swap, and a decoder swap must be able to re-attach to
   the existing packet stream at the current position.
4. **Hardware device coupling.** Hardware frames carry the decoder's D3D11
   device/context pointers (`VideoDecoder.D3D11DevicePointer`, frame fields at
   `VideoDecoder.cs:1069-1072`); the shared-device invariant is the frame-release
   safety mechanism (see `007B_NOTE_HardwareFrameOwnershipMap.md`). Splitting
   demux from decode must not disturb which D3D11 device owns the frames.
5. **BASS push topology.** Audio is a BASS **push** stream fed by FFmpeg PCM
   (`CreateBassStreams`, `:688`); audio-only playback is a BASS **pull** stream
   (`AudioStreamManager`). A shared FFmpeg demux only helps the push path; it
   does not unify with BASS pull and should not try to.
6. **Archive temp-file dedupe.** Two temp files per archive entry today
   (`VideoPipeline.OpenBytes` `:251` + `VideoAudioEngine.OpenBytes` `:301`). A
   shared demux is the natural place to collapse this to one temp file or a
   custom AVIO; scope that explicitly.
7. **Future consumers don't exist yet.** Subtitles/multi-track/chapters (013A),
   streaming/network input (014A). Designing the fan-out API now risks
   speculative generality; the stage rationale lists them but the safe first
   implementation is video+audio only, with the API shaped so those can be added
   later without another rewrite.

## 9. Validation

This sub-stage is **mapping only** — no code changed, so per the working-notes
"Validation Shortcut" (docs/mapping only): **no build required; confirm no
source edits.** Only `docs/roadmap/012A_NOTE_SharedDemuxerInputLayerMap.md` was
added. `STAGE_MANIFEST.md` is unchanged and 012A remains FUTURE.

## 10. Stop conditions for any follow-up (012A.2)

- Do **not** begin source edits until risks §8.1–§8.3 (read arbitration, seek
  semantics, independent lifetime) have an agreed design — those are where a
  naive merge silently breaks A/V sync, seek feel, or device-lost recovery.
- Do **not** couple audio-stream lifetime to a video-decoder rebuild
  (`SwapVideoDecoder` must keep working).
- Do **not** force a single shared seek at the loop boundary — gapless audio
  looping depends on the audio wrapping its own decoder independently.
- Do **not** fold CDG or BASS-pull audio-only into the FFmpeg shared demux;
  they are different topologies.
- Keep the stage FUTURE and out of the active manifest until the above is
  resolved.
