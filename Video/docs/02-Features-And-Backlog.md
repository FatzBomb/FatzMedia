# Features And Backlog

## Current Feature Matrix

| Feature | Current status |
|---|---|
| Audio playback | Supported through BASS. |
| Volume / pitch / tempo | Supported for audio and video audio through active `IAudioEngine`. |
| Audio-file normalization | Supported with sampled peak analysis, cancellation, caching, and readiness signaling. |
| CDG playback | Supported and protected. |
| Paired audio+CDG | Supported for side-by-side and archive media. |
| ZIP/archive support | Supported for karaoke audio/CDG bundles; archive video uses temp files. |
| Standalone CDG | Supported through `PlaybackClock`. |
| Video playback | Supported through FFmpeg video decode plus separate BASS audio path. |
| Video without audio | Supported through virtual clock timing. |
| Direct image playback | Supported for foreground still images. |
| Karaoke backgrounds | Supported through explicit, sidecar, and internal zip image/video backgrounds. |
| Metadata | Supported through the metadata wrapper. |
| Host-owned render surface | Supported through `AttachSurface(Image)` and `AttachSurface(Image, Panel)`. |
| Automatic rendering selection | Required behavior. Host UI should not expose render/decode mode choices. |
| MIDI playback | Not working yet. Planned fix. |
| Trim silence | Planned. |
| Fade / crossfade | Planned. |
| Hue / tint / brightness / contrast | Planned. |
| FFmpeg hardware decode | Auto probes D3D11VA, D3D12VA, and DXVA2 with software fallback. |
| True accelerated WPF presentation | D3D11 hardware frames can present through the D3DImage/shared-surface path without CPU BGRA transfer; needs build/runtime validation. |
| Video-audio normalization | Undecided; currently reports `1.0`. |

## Current Stable Shape

- The active control project uses the new `Pipeline/` path.
- The old concrete orchestration has been removed from the active project.
- The player is a WPF host/test shell with MVVM state and host-owned XAML.
- Native BASS/FFmpeg DLLs are host-owned and supplied through `DLLPath.Bass` and `DLLPath.FFmpeg`.
- Missing required/optional native DLLs produce readable diagnostics.
- File picker filters come from `MediaPlayer.OpenFileDialogFilter`.
- Rendering selection is automatic.
- The old embedded `MediaPlayerControl` has been removed.
- `RenderSurface` attaches renderer output to a host-owned `Image` and optional background `Panel`.
- Loop/end handling is driven by pipeline media-ended events rather than controller polling.

## Active Tuning Work

1. Improve video playback smoothness. Subtle frame drops are still visible compared with professional players.
2. Improve video seek catch-up after large seeks.
3. Continue tuning live scrub cadence. Current first-pass values are 100ms for audio/CDG and 200ms for video.
4. Decide whether the control needs a formal seek session API: begin preview, coalesce preview seeks, commit, cancel.
5. Review remaining video frame copies, decoder queue behavior, dispatcher frequency, and background video decode cost.

## Normalization Work

Current audio-file normalization has:

- Separate BASS analysis handle.
- Quick/deep sampled peak analysis.
- Cancellation/generation guard.
- Gain cache.
- Ready signaling for play-on-load, including unchanged `1.0` gain.

Remaining:

1. Decide whether video-audio normalization is unsupported or gets its own analysis path.
2. If better perceived loudness matching is needed, add a delayed LUFS/R128-style pass rather than blocking startup.

## Hardware Work

1. Validate FFmpeg D3D11VA/D3D12VA/DXVA2 decode across AV1, H.264, H.265, MPEG-2, and fallback cases.
2. Preserve software fallback and keep selection automatic.
3. Validate the D3DImage/shared-surface presentation path and keep `WriteableBitmap` fallback.
4. Validate the D3D11 decoder-frame no-copy path and keep CPU transfer fallback for non-D3D11 hardware frames.
5. Keep CDG protected; do not force CDG through the video hardware path unless exact pixel behavior is verified.
6. Add diagnostics for actual decode method, presentation method, fallback reason, and hardware device.
7. Design a future hardware-selection layer before enabling CUDA/vendor-specific paths in Auto. It should evaluate codec, FFmpeg-reported hardware configs, GPU/vendor, driver support, available memory, decode failure history, presentation compatibility, and resource pressure. D3D11VA/D3D12VA/DXVA2 remain the stable default set until that selector is verified.

## Feature Backlog

Audio:

1. Fix and validate MIDI playback. `bassmidi.dll` alone is not enough; verify sound-font requirements, stream creation, accepted file types, and test-player filters.
2. Add trim-silence detection/playback behavior.
3. Add fade primitives.
4. Add crossfade behavior after playlist/next-item ownership is clear.
5. Consider native BASS loop flags as an audio-only optimization, while keeping control-level loop policy pipeline-driven so audio+CDG and video reset all synchronized streams correctly.

Visual:

1. Add hue, tint, brightness, and contrast controls.
2. Prefer shader/accelerated processing for video/image effects once the true hardware surface path exists.
3. Keep a software fallback for CDG and bitmap-backed rendering paths.
4. Do not apply visual effects to CDG lyrics unless explicitly requested; CDG readability is protected.

Architecture:

1. Keep improving playback isolation so host UI stalls do not affect decode/feed/render work.
2. Coalesce high-frequency position/frame events.
3. Keep cancellation/disposal rules tight for load/close/seek.

## Removed Active Orchestration

These are gone from the active project and should stay reference-only:

```text
Core/MediaEngine.cs
Core/Video/AudioVideoEngine.cs
Core/Video/VideoTimer.cs
Core/Timing/UnifiedRenderTimer.cs
Core/Audio/SilenceEngine.cs
Core/Audio/SilenceClock.cs
Core/Abstractions/IMediaClock.cs
Core/Abstractions/ILibrary.cs
Runtime/Defaults.cs
Runtime/Host.cs
Runtime/MediaPlayerServiceProvider.cs
Core/Audio/LoadBass.cs
Controllers/MediaTime.cs
Wpf/MediaPlayerControl.xaml
Wpf/MediaPlayerControl.xaml.cs
```

Use `legacy/` only to audit and import small pieces when a current pipeline feature truly needs them.
