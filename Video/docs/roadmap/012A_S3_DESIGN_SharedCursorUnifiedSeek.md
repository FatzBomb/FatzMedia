<!-- Design note for revised S3 (shared cursor + unified seek). Docs only — no source changed. -->

# 012A.S3 Design — Shared Cursor + Unified Seek Semantics

Parent plan: `docs/roadmap/012A.2_PLAN_SharedDemuxerImplementation.md` (revised S3)
Mapping: `docs/roadmap/012A_NOTE_SharedDemuxerInputLayerMap.md`
Reverted attempt: `docs/roadmap/updates/012A_S3/StageUpdate.md`

This note resolves the five design challenges identified in the revised plan.
It is the gate for promoting revised S3 to BACKLOG. No source edits are
authorized by this document.

## Architecture overview

```
VideoPipeline.Open(path)
  → SharedDemuxer.Open(path)           ← one AVFormatContext, one read cursor
  → VideoDecoder.FromConfiguration(config, sharedDemuxer)
      → VideoDecoder.Open(path)         ← skips container open (already open)
      → SharedDemuxer.RegisterVideoStream(videoIndex)
  → VideoAudioEngine(provider, deviceIndex, sharedDemuxer)
      → VideoAudioEngine.Open(path)     ← skips container open (already open)
      → SharedDemuxer.RegisterAudioStream(audioIndex)
```

Both engines share one `SharedDemuxer`. Each engine keeps its own codec
context, frame queue, discard window, and BASS/presentation plumbing — only
the container read/seek is shared.

## Challenge 1: Coordinated seek

### Problem

`VideoPipeline.SeekCore` (`VideoPipeline.cs:556`) currently calls two
independent container seeks:
1. `_audioEngine.Seek(target)` → `VideoAudioEngine.SeekDecoder` (`:816`)
   → `_demuxer.Seek(_audioStreamIndex, ...)`.
2. `SeekVideoNearTarget(target)` → `VideoDecoder.Seek` (`:1025`)
   → `_demuxer.Seek(_videoIndex, ...)`.

With one shared cursor, the second `av_seek_frame` overrides the first.

### Decision: single coordinated seek

Replace the two independent container seeks with **one** `SharedDemuxer.Seek`
call, driven by `VideoPipeline.SeekCore`. The engines' seek methods are split
into "container seek" (done by `SharedDemuxer`) and "codec flush + discard
window" (done by each engine).

**New `SharedDemuxer.Seek` API:**
```csharp
// Seeks the container once, flushes both packet queues, clears both EOF flags.
// Called by VideoPipeline.SeekCore — NOT by the engines directly.
public int Seek(int streamIndex, long timestamp, int flags);
```

**Modified engine seek flow:**

`VideoPipeline.SeekCore` does:
1. `_sharedDemuxer.Seek(seekStreamIndex, timestamp, flags)` — one container
   seek, flushes both queues, clears both EOFs.
2. `_videoDecoder.OnContainerSeeked(target, exact)` — flushes video codec
   buffers, clears video `EndOfStream`, sets `_discardFramesBeforeSeconds`.
   Does NOT seek the container.
3. `_audioEngine.OnContainerSeeked(target)` — flushes audio codec buffers,
   sets `_discardAudioBeforeSeconds`. Does NOT seek the container.

**Which stream index to seek?** Seek the **video** stream index with
`AVSEEK_FLAG_BACKWARD` (same as `VideoDecoder.Seek` does today). FFmpeg's
`av_seek_frame` with a specific stream index seeks to the nearest keyframe
before the target in that stream's timebase. Audio packets before the target
are discarded by `_discardAudioBeforeSeconds`. This matches the current
behavior where video's seek is the "real" seek and audio's seek is to the
same target.

**Engine API changes:**
- `VideoDecoder`: add `internal void OnContainerSeeked(double target, bool exact)`
  that does the flush + discard + EndOfStream=false, but NOT the container
  seek. The existing `Seek(seconds, exact, preserveQueue)` method is kept for
  the standalone/probe/background-video path (owns its own demuxer).
- `VideoAudioEngine`: add `internal void OnContainerSeeked(double target)`
  that does the flush + discard, but NOT the container seek. The existing
  `SeekDecoder(seconds)` is kept for the standalone path.

**Dual-mode pattern (same as reverted S3):** When an engine owns its demuxer
(`_ownsDemuxer = true`), its `Seek` method seeks the container + flushes +
discards (current behavior). When sharing (`_ownsDemuxer = false`), `Seek`
delegates to `OnContainerSeeked` (flush + discard only); the container seek
is done by `VideoPipeline` via `SharedDemuxer.Seek`.

### What stays the same

- SmartSeek's video-flexible behavior: `SeekVideoNearTarget` still calls
  `VideoDecoder.Seek` (or `OnContainerSeeked`), which sets
  `_discardFramesBeforeSeconds` only for commit seeks (not preview). The
  decode budget / max-attempts / frame-distance logic in
  `DecodeForSeekTarget` is unchanged.
- The seek worker pump (`SeekWorkerLoop`) is unchanged.
- Post-seek recovery (`PreparePostSeekVideoRecovery`, `BeginPostSeekPresentationRecovery`)
  is unchanged.

## Challenge 2: Gapless loop strategy

### Problem

The current gapless loop relies on **independent** cursors:
1. `TryPreSeekLoopStart` (`:1717`) seeks the **video** decoder to 0 while
   audio is still playing buffered audio near EOF. Video decodes position-0
   frames behind the tail.
2. `VideoAudioEngine.WrapDecoderForLoop` (`:370`) seeks the **audio** decoder
   to 0 at EOF, without stopping the BASS channel.
3. `CommitAudioLoopWrap` (`:414`) drops stale tail frames, presents the first
   position-0 frame, re-anchors the clock.

With a shared cursor, step 1 (video pre-seek to 0) moves the cursor away from
audio's read position near EOF, disrupting audio.

### Decision: option (e) — drain-audio-then-seek (gapless for both)

**Keep `TryPreSeekLoopStart`, but replace the decoder seek with
`SharedDemuxer.PrepareLoopWrap()`.** When video hits EOF, drain remaining
audio packets into the audio queue, then seek the shared cursor to 0. Video
gets position-0 frames immediately (gapless video). Audio plays buffered
near-EOF packets, then wraps (gapless audio).

**Revised loop-wrap flow:**

1. Video decoder reaches EOF → `SharedDemuxer.ReadVideoPacket` returns
   `AVERROR_EOF` → `_videoEof = true`. Tail frames still queued in
   `VideoDecoder._frameQueue`. Video presentation continues showing tail
   frames from the queue.
2. `TryPreSeekLoopStart` detects `decoder.EndOfStream && decoder.FrameCount > 0`.
   Calls `_sharedDemuxer.PrepareLoopWrap()`:
   a. **Drain phase:** Read remaining packets from the underlying demuxer
      until it returns EOF. Buffer audio packets into `_audioPackets`.
      (Video packets are unlikely since video already hit EOF, but if any
      appear, buffer them into `_videoPackets`.)
   b. **Seek phase:** `av_seek_frame` to 0 on the video stream index.
   c. Flush `_videoPackets` (stale tail packets). Clear `_videoEof`.
   d. Do **NOT** flush `_audioPackets` (near-EOF audio preserved).
   e. Do **NOT** clear `_audioEof` (audio will discover EOF when its queue
      drains).
3. `TryPreSeekLoopStart` calls `decoder.OnContainerSeeked(0, exact: false)`
   which clears `EndOfStream`, flushes codec buffers, sets discard window.
4. `TryPreSeekLoopStart` calls `SeamlessLoopRestart.TopUpDecode` → video
   reads position-0 packets from the shared cursor (already at 0 from step 2b).
   Position-0 frames are decoded and queued **behind** the tail frames.
   **GAPLESS VIDEO** — tail frames present first, then position-0 frames.
5. Audio continues reading from `_audioPackets` (near-EOF packets drained
   in step 2a). BASS push channel never stops. **GAPLESS AUDIO.**
6. Audio's queue drains → `ReadAudioPacket` sees `_audioEof = true` → returns
   EOF. `DecodeAndPushAudioCore` calls `WrapDecoderForLoop`.
7. `WrapDecoderForLoop` calls `SeekDecoder(0)`. In shared mode, this calls
   `SharedDemuxer.ClearAudioEof()` (NOT `SharedDemuxer.Seek` — the cursor is
   already at 0 from step 2b). This clears `_audioEof` only. Does NOT flush
   video packets or clear video EOF (video is already past its wrap).
8. Audio reads position-0 packets from the shared cursor, pushes to BASS.
   `CompletedLoopCount` advances.
9. `CommitAudioLoopWrap` fires (presentation thread detects
   `CompletedLoopCount` advanced):
   - Video already has position-0 frames (from step 4).
   - `DropStaleLoopTailFrames` drops any remaining tail frames.
   - Presents first position-0 frame.
   - Re-anchors presentation clock to audio position (now near 0).

**What changes:**
- `TryPreSeekLoopStart` calls `_sharedDemuxer.PrepareLoopWrap()` instead of
  `decoder.Seek(0)`. The drain-then-seek preserves audio's read position
  (near-EOF packets are buffered) while moving the cursor to 0 for video.
- `VideoDecoder.Seek` in shared mode delegates to `OnContainerSeeked` (flush +
  discard, no container seek). `TryPreSeekLoopStart` calls
  `OnContainerSeeked(0, exact: false)` after `PrepareLoopWrap`.
- `VideoAudioEngine.SeekDecoder(0)` in shared mode (called by
  `WrapDecoderForLoop`) calls `SharedDemuxer.ClearAudioEof()` instead of
  `SharedDemuxer.Seek(0)`. The cursor is already at 0; only audio's EOF needs
  clearing.
- `CommitAudioLoopWrap` is unchanged — it still drops stale tail frames,
  presents position-0 frame, re-anchors clock.

**What stays the same:**
- Audio gaplessness: the BASS push channel never stops. Audio plays buffered
  near-EOF packets (drained in step 2a), then wraps to position-0 packets.
- Video gaplessness: position-0 frames are decoded and queued behind tail
  frames (step 4). Tail frames present first, then position-0 frames.
- `CommitAudioLoopWrap`'s clock re-anchoring, position update, and
  `PositionChanged` event.
- `ShouldRestartLoopPlayback` / `TryRestartLoopPlayback` (reactive fallback
  for non-audio video).
- The `SeamlessLoopRestart.TopUpDecode` call in `TryPreSeekLoopStart`.

**Why the drain is fast:** Step 2a reads packets via `av_read_frame` only
(demuxing, no decoding). Even 30 seconds of remaining audio drains in
milliseconds — it's just reading and queueing pointers. The audio engine
consumes the queued packets at its own pace via the normal decode loop.

## Challenge 3: Per-consumer EOF

### Problem

The reverted S3 propagated EOF across streams: when video hit EOF, it set
`_audioEof = true`. This caused audio to see EOF prematurely (audio might not
read from the demuxer for seconds due to the 2–4s push buffer), triggering an
early loop wrap.

### Decision: no cross-stream EOF propagation

Each consumer discovers EOF on its own next read. `ReadVideoPacket` only sets
`_videoEof`. `ReadAudioPacket` only sets `_audioEof`. The only time both EOFs
are cleared together is `SharedDemuxer.Seek` (which flushes both queues and
clears both EOFs — see Challenge 1 and 2).

This means video may hit EOF before audio (video streams often end before
audio in music videos). Video's EOF is set; audio continues reading. When
audio finally hits EOF, `WrapDecoderForLoop` seeks the shared cursor to 0,
clearing both EOFs. This is correct.

## Challenge 4: `SwapVideoDecoder` / device-lost recovery

### Problem

`SwapVideoDecoder` (`:1393`) disposes and recreates the `VideoDecoder` at the
current position. With a shared demuxer, the new decoder must re-attach to the
existing shared demuxer without disposing it, and audio must keep running
undisturbed.

### Decision: shared demuxer survives decoder swap

`SwapVideoDecoder` passes the existing `_sharedDemuxer` to the new
`VideoDecoder` constructor (same as the reverted S3 attempted). The new
decoder's `Open(path)` skips container-open (`!_demuxer.IsOpen`), finds the
video stream, and seeks to the current position via
`SharedDemuxer.Seek` (or `OnContainerSeeked` if the pipeline coordinates the
seek).

**Key difference from reverted S3:** The reverted S3's `VideoDecoder.Close`
only closed the demuxer if `_ownsDemuxer` was false — but `SwapVideoDecoder`
calls `_videoDecoder.Dispose()` which calls `Close()` which calls
`_demuxer.Close()`. With the `_ownsDemuxer` guard, the shared demuxer is NOT
closed when the video decoder is disposed. This is correct and was already
working in the reverted S3 (the device-lost recovery path was not the bug).

**`HardwareDecodeDeviceAvailable` probe** (`:1373`): Creates a throwaway
`VideoDecoder` **without** a shared demuxer. It gets its own `SharedDemuxer`
(or `MediaDemuxer` — see Challenge 5). This is correct and must not change.

## Challenge 5: Hardware probe isolation

### Problem

`HardwareDecodeDeviceAvailable` (`:1373`) opens a throwaway `VideoDecoder` to
test whether the D3D11 hardware-decode device is back. This must not disturb
the live shared demuxer's read cursor.

### Decision: probe uses its own demuxer

The probe creates `new VideoDecoder(VideoDecodingMethod.Hardware, ...)` with
**no** `sharedDemuxer` parameter. It gets its own `MediaDemuxer` (or
`SharedDemuxer` in single-stream mode — functionally identical since no
audio consumer is registered). The probe opens the file independently, tests
hardware, and disposes. The live shared demuxer is untouched.

This is the same as the reverted S3 and was not a bug.

## SharedDemuxer API (revised)

```csharp
internal sealed unsafe class SharedDemuxer : IDisposable
{
    // Container lifecycle
    public bool Open(string path);
    public bool IsOpen { get; }
    public uint StreamCount { get; }
    public long FormatDurationTicks { get; }

    // Stream discovery
    public int FindBestStream(AVMediaType mediaType);
    public AVStream* GetStream(int index);
    public void SetStreamDiscard(int index, AVDiscard discard);
    public void RegisterVideoStream(int index);
    public void RegisterAudioStream(int index);

    // Per-stream packet read (fan-out)
    // Non-matching packets are buffered for the other consumer.
    // EOF is per-consumer: ReadVideoPacket only sets _videoEof,
    // ReadAudioPacket only sets _audioEof. No cross-stream propagation.
    public int ReadVideoPacket(AVPacket* packet);
    public int ReadAudioPacket(AVPacket* packet);

    // Coordinated seek: seeks container once, flushes BOTH queues,
    // clears BOTH EOF flags. Called by VideoPipeline.SeekCore.
    public int Seek(int streamIndex, long timestamp, int flags);

    // Loop-wrap support (see Challenge 2, option (e)):
    // Drains remaining audio packets into _audioPackets, seeks cursor to 0,
    // flushes video queue, clears video EOF. Does NOT touch audio queue/EOF.
    // Called by VideoPipeline.TryPreSeekLoopStart.
    public void PrepareLoopWrap();

    // Clears _audioEof only (cursor already at 0 from PrepareLoopWrap).
    // Does NOT seek, does NOT flush video. Called by VideoAudioEngine.WrapDecoderForLoop.
    public void ClearAudioEof();

    // Lifecycle
    public void Close();
    public void Dispose();
}
```

**Key difference from reverted S3:** No `SeekVideo`/`SeekAudio` split. One
`Seek` method that flushes both queues and clears both EOFs. This is the
fix for the gapless loop: when audio wraps and calls `SeekDecoder(0)`, the
single `Seek` clears video's EOF too, so video can read position-0 packets
after the wrap.

## Engine API changes

### VideoDecoder

- Constructor gains `SharedDemuxer? sharedDemuxer = null` parameter (same as
  reverted S3).
- `_ownsDemuxer` flag (same as reverted S3).
- `Open`: skips container-open if already open; skips `AVDISCARD_ALL` when
  shared; self-registers video stream (same as reverted S3).
- `DecodeNextFrame`: `_demuxer.ReadPacket` → `_demuxer.ReadVideoPacket`
  (same as reverted S3).
- **New:** `internal void OnContainerSeeked(double target, bool exact)` —
  flushes codec, clears `EndOfStream`, sets `_discardFramesBeforeSeconds`.
  Does NOT seek the container. Called by `VideoPipeline.SeekCore` after
  `SharedDemuxer.Seek`.
- `Seek(seconds, exact, preserveQueue)`: when `_ownsDemuxer`, seeks container
  + flushes + discards (current behavior, for standalone/probe/background).
  When sharing, delegates to `OnContainerSeeked` (no container seek).
- `Close`/`Dispose`: only close/dispose if `_ownsDemuxer` (same as reverted S3).

### VideoAudioEngine

- Constructor gains `SharedDemuxer? sharedDemuxer = null` parameter (same as
  reverted S3).
- `_ownsDemuxer` flag (same as reverted S3).
- `Open`: skips container-open if already open; self-registers audio stream
  (same as reverted S3).
- `DecodeAndPushAudioCore`: while-loop with `ReadPacket` → single
  `ReadAudioPacket` call (same as reverted S3).
- **New:** `internal void OnContainerSeeked(double target)` — flushes codec,
  sets `_discardAudioBeforeSeconds`. Does NOT seek the container. Called by
  `VideoPipeline.SeekCore` after `SharedDemuxer.Seek`.
- `SeekDecoder(seconds)`: when `_ownsDemuxer`, seeks container + flushes +
  discards (current behavior, for standalone). When sharing, calls
  `SharedDemuxer.ClearAudioEof()` (just clears `_audioEof`; cursor is already
  at 0 from `PrepareLoopWrap`). This is the special case for
  `WrapDecoderForLoop` — see Challenge 2.
- `CloseCore`/`Dispose`: only close/dispose if `_ownsDemuxer` (same as reverted S3).

### VideoPipeline

- `Open`: creates `SharedDemuxer`, opens it, passes to both engines (same as
  reverted S3).
- `SeekCore`: calls `_sharedDemuxer.Seek(...)` once, then
  `_videoDecoder.OnContainerSeeked(target, exact)` and
  `_audioEngine.OnContainerSeeked(target)`.
- `TryPreSeekLoopStart`: **modified** — calls
  `_sharedDemuxer.PrepareLoopWrap()` instead of `decoder.Seek(0)`, then
  `decoder.OnContainerSeeked(0, exact: false)`, then
  `SeamlessLoopRestart.TopUpDecode` (same as today).
- `CommitAudioLoopWrap`: unchanged — drops stale tail frames, presents
  position-0 frame, re-anchors clock. Video already has position-0 frames
  from `TryPreSeekLoopStart`'s `PrepareLoopWrap` + `TopUpDecode`.
- `SwapVideoDecoder`/`TryFallbackToSoftwareVideo`: pass `_sharedDemuxer` to
  new decoder (same as reverted S3).
- `Close`: disposes `_sharedDemuxer` after both engines (same as reverted S3).

## What the reverted S3 got right (kept)

- Dual-mode `_ownsDemuxer` pattern.
- `ReadVideoPacket`/`ReadAudioPacket` fan-out with per-stream queues.
- `RegisterVideoStream`/`RegisterAudioStream` self-registration.
- Skip container-open if already open.
- Skip `AVDISCARD_ALL` when shared.
- `SwapVideoDecoder` passes shared demuxer.
- Hardware probe uses own demuxer.

## What the reverted S3 got wrong (fixed)

| Bug | Cause | Fix |
|---|---|---|
| Gapless loop freeze | `SeekVideo`/`SeekAudio` split: video pre-seek moved cursor, audio's EOF was set by video's EOF, audio couldn't read after video pre-seek | `PrepareLoopWrap()`: drain audio packets first, then seek to 0. Audio's near-EOF packets are preserved in queue. `ClearAudioEof()` for audio wrap (cursor already at 0). `TryPreSeekLoopStart` preserved. |
| AV1 seek freeze | `SeekAudio` then `SeekVideo` on same cursor: second seek overrides first | Single `Seek` in `SeekCore`, then `OnContainerSeeked` for each engine (flush + discard only, no container seek). |
| Premature audio EOF | Cross-stream EOF propagation: video EOF set `_audioEof` | No cross-stream propagation. Each consumer discovers EOF on its own read. |

## Validation plan

1. `dotnet build` (Debug) — clean.
2. `dotnet build -c Release` — **0 warnings** (warnings-as-errors).
3. `scripts/check-wpf-boundary.ps1` — pass.
4. `scripts/check-presentation-parity.ps1` — pass.
5. `dotnet test -c Release` — no regressions.
6. **Full manual smoke** (all Must-Not-Break items):
   - Video-with-audio playback (H.264+AAC, HEVC HW, AV1 SW, AVI).
   - A/V sync.
   - Seek (rapid scrubbing — preview + settle).
   - **Gapless audio-backed loop** (critical test — audio must be gapless,
     video may freeze briefly at boundary).
   - Device-lost recovery (`SwapVideoDecoder` reuses shared demuxer).
   - Background video (own SharedDemuxer, unaffected).
   - CDG-paired playback.
   - Media switch (close + open).
   - Extended soak (10+ min).

## Stop conditions

- Any A/V desync after seek → revert to S2.
- Any audio gap at loop boundary → revert to S2.
- Any `SwapVideoDecoder` / device-lost regression → revert to S2.
- Any starvation/overflow → revert to S2.

## Status

Design only. No source changed. This note must be reviewed before promoting
revised S3 to BACKLOG. Parent 012A stays FUTURE.
