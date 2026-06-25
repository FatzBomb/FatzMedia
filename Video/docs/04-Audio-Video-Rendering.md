# Audio, Video, Rendering, And Effects

## Audio Effects

The control supports these effects through the active `IAudioEngine`:

- Volume
- Pitch
- Tempo
- Audio-file normalization

Effect routes:

```text
MediaPlayer.Effects.Volume -> MediaEffects -> active IAudioEngine.SetVolume(...)
MediaPlayer.Effects.Pitch  -> MediaEffects -> active IAudioEngine.SetPitch(...)
MediaPlayer.Effects.Tempo  -> MediaEffects -> active IAudioEngine.SetTempo(...)
```

`MediaEffects` resolves the active audio engine after media load, so audio files and video-audio files use the same public effect surface.

## Normalization

Audio-file normalization is peak-based and optimized for fast startup:

- Uses a separate BASS decode/analysis handle.
- Runs quick sampled peak analysis first.
- Runs deeper sampled peak analysis after that.
- Uses cancellation/generation guarding so stale analysis cannot update a newly loaded file.
- Caches gain by file identity and archive entry key/size.
- Exposes `IAudioEngine.IsNormalizationReady`.
- Raises normalization ready/change notification even when the resulting gain remains `1.0`.
- Keeps the current media's gain through `Stop()`.

`VideoAudioEngine` currently reports normalization gain as `1.0`. Video-audio normalization should either get a separate FFmpeg/BASS analysis path or remain clearly unsupported.

## Video Sync

For video files, video frames and audible audio are handled separately:

```text
Video file
  -> FFmpeg video decoder
  -> separate FFmpeg audio decode into BASS push/tempo stream, when audio exists
  -> BASS audio clock as master timing source
  -> video frame selection follows the master clock
```

For silent video:

```text
Silent video
  -> FFmpeg video decoder
  -> PlaybackClock / ClockAudioEngine
  -> video frame selection follows virtual clock
```

Implemented:

- Video playback is clock-gated instead of presenting at decoder speed.
- Video audio seeks through the BASS-backed video audio path.
- Video frame queues are flushed/rebuilt on seek.
- Raw BGRA video buffering is bounded by resolution so large/high-quality files do not queue hundreds of MB of converted frames.
- Video BGRA frame buffers are pooled and reused so normal playback does not allocate a new multi-megabyte array per frame.
- The decode loop yields between small decode batches so WPF render ticks are less likely to be blocked by frame conversion work.
- Video presentation uses a small audio-following presentation clock so BASS remains master while render ticks interpolate between audio position samples.
- Video presentation refuses to present very stale frames after seek/clock jumps so old decoded frames do not appear as extreme visual stutter.
- Hardware decoder startup falls back to software if a selected hardware-capable decoder opens but produces no usable startup frame.
- D3D11 hardware-decoded frames can now remain on the GPU: FFmpeg D3D11 textures are passed through the pipeline and presented with a D3D11 video processor into the WPF `D3DImage` shared surface.
- Video decode and presentation use dedicated playback scheduler workers so host UI stalls do not own the video clock.
- Silent video uses `PlaybackClock`.
- Volume, pitch, and tempo route to the active video audio engine.

Still active work:

- Reduce subtle frame skips/choppiness.
- Improve video seek catch-up after large seeks.
- Validate FFmpeg hardware decode, D3DImage presentation, and fallback behavior.
- Decide whether video-audio normalization gets real analysis or remains unsupported.

## Rendering

Rendering selection is automatic. Host apps should not expose DirectX/software/hardware decode choices.

FFmpeg video decode currently prefers decoder implementations that report D3D11VA, D3D12VA, or DXVA2 support, then falls back to software decoding. D3D11VA frames use the no-copy presentation path when FFmpeg provides `AV_PIX_FMT_D3D11`; other hardware frame types still use the CPU transfer path until they have an efficient WPF-compatible bridge.

D3D12VA and DXVA2 are part of the hardware decode probe, but the true no-copy WPF surface path is D3D11-first because WPF presentation is ultimately a `D3DImage` shared-surface bridge. CUDA and other vendor-specific decode paths should stay out of the normal Auto path until there is a real hardware-selection layer. That layer should choose based on the video codec, FFmpeg hardware configs, GPU/vendor and driver support, available resources, known failure history, and whether the decoded surface can be presented efficiently.

The D3D11 presentation path uses a shared texture bridged into WPF through `D3DImage`. Software video frames upload into that texture; D3D11 hardware frames are converted/presented on the GPU with the D3D11 video processor. `WriteableBitmap` remains the fallback path for CDG, software rendering, and systems where the shared-surface bridge fails.

Current rules:

- CDG intentionally uses the CPU bitmap path for exact lyric rendering.
- Background videos are silent independent loops and do not seek with audio/CDG.
- Diagnostics should be read-only: actual decode method, presentation method, fallback reason, and hardware availability.

Remaining hardware work:

1. Validate FFmpeg hardware decode on representative files and confirm metadata reports the active method.
2. Validate D3DImage/shared-surface presentation and fallback behavior.
3. Validate D3D11 no-copy presentation on representative AV1, H.264, and H.265 files.
4. Keep CDG protected during hardware/rendering work.
5. Add an automatic hardware selector for CUDA/vendor-specific paths only after D3D11VA/D3D12VA/DXVA2 behavior is stable and diagnostics can explain every fallback.

## Video Performance Diagnostics

The video path emits compact performance lines while playing:

- `[VideoDecoder:Perf]` shows average per-frame decode-side transfer, scale, frame-buffer allocation, and total preparation cost.
- `[VideoPipeline:Perf]` shows presented frames, dropped catch-up frames, max lateness, and queue depth.
- `[VorticeD3D11:Perf]` shows average D3D11 upload or hardware-present cost for the WPF presentation surface.

Use these to decide whether the next optimization should target decode/format conversion, presentation upload, or frame scheduling.
