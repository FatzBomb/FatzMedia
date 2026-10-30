# ENG-039 batch 4c: shared helpers for duplicated code (R5-R8)

2026-10-28. Branch `claude/festive-newton-yvvib5-b4c` (on top of 4b). Status: implemented,
pending manual smoke. Meant to change nothing visible except where noted.

## What changed

**Adapters (R5).**
- `LatestFrameMailbox` (Core, public) is the latest-frame hand-off of the WPF composited host and
  the Avalonia and WinForms frame sinks; each adapter keeps its UI-thread marshalling and its
  `Clear` behaviour. Two idle buffers circulate instead of re-allocating under contention; the
  2x spare cap now applies to every adapter; sizes are computed in `long` (an oversized frame is
  dropped; WPF used to throw on the presentation thread).
- `KaraokeLayout.Fit` and `PresentationPlacement.Center` replace three copies each of the 4:3
  fit and second-screen centring math.
- `MediaSurfaceBookkeeping` holds the three media surfaces' surface number, effective id, path
  and reason (with its Info line) and the once-only attach-refused warning. Surface numbers are
  now unique per process. `check-presentation-parity.ps1` looks for the new call.
- `LightweightPlayerController.RunQueuedReloadAsync` is the one queued-reload runner of the
  karaoke and background controls. No separate `LightweightSurfaceCore`: the rest is two small
  copies. WPF's visibility-writer merge was not needed.

**Player helpers (R6).** `PlayerRendererRules.DescribeDecoding` is the one decoding-status table
(pinned by a `[Theory]`). Wording change: the Windows players' Vulkan line on a device without
Vulkan Video now says "software decoding (the device has no Vulkan Video decoding)". No
`WindowsPlayerEngineFactory`: the two factories share too little.

**Renderers (R7).**
- Core: `MediaCompositionFrame.ResolveClearArgb`, `ColorMath.PremultiplyBgra`,
  `MediaPresentationFrameSinkHandle.TryResolve(surface, out sink)`,
  `NullSubtitleRasterizer.Instance` and `CompositionPresentOutcome` (primary decides, otherwise
  any target) replace the per-renderer copies.
- Vulkan: one `AllocateMemory`, one `VulkanResult.Check`, a shared `CreateFramebuffer`. The
  Linux import path, which passed memory type -1 to the driver, now throws a clear error.
- Direct3D11: `D3D11HardwareFramePresenter` replaces three pass-through hardware-frame layers;
  the DirectComposition root transform (always identity) is gone. Device-loss handling from
  batch 2 is unchanged.

**Audio and misc (R8).** `FfmpegAudioDecoderSetup` (decoder and resampler open, twice before),
`PollingLoop` (the Windows and Linux device monitors; Core, public, as the packages cannot see
Core internals; `Stop` returns at once, and from the callback without joining itself),
`OpaqueTokenRegistry<T>` (frame-sink and shared-image tokens), `DiscardAllStreamsExcept` (three
discard loops) and `CdgPipeline` using `SeekHelpers.NormalizeSeekTarget`.

New tests: `LatestFrameMailboxTests`, `AdapterLayoutTests`, `MediaSurfaceBookkeepingTests`,
`PollingLoopTests`, `OpaqueTokenRegistryTests`, clear-mode, premultiply, `TryResolve` and
present-outcome tests, and the decoding-text theory.

Known flaky, unrelated: `LinuxNetworkVideoNativeTests` network-timing cases fail about once in a
full run under load and pass alone (seen at the batch 3 base too).

## Manual smoke (Windows)

1. WPF `--renderer=software` (composited): video plays smoothly; maximize, restore and keep
   playing without stutter or memory growth; Stop clears without the old frame flashing back.
2. The Log shows one "Surface #N 'primary': ..." path line per surface.
3. Karaoke surfaces keep a centred 4:3 picture wide and tall; the second screen is centred on
   its monitor (WPF, Avalonia, WinForms).
4. A missing lightweight-surface Source gives one Warning "Reloading the source failed: ...".
5. Direct3D11 renderer with hardware decoding, each surface kind (native HWND,
   DirectComposition with live resize and maximize, shared texture, frame sink with a partly
   transparent clear colour), plus a second screen: video on both, detaching the second screen
   leaves the primary playing. HDR10 on an HDR monitor still uses scRGB.
6. TDR during hardware playback: one step-named device-lost line, playback recovers.
7. Vulkan on Windows to a shared-texture surface: no "No memory type" errors.
8. Audio: stereo, mono and 5.1 files play correctly; a file with an unsupported audio stream
   fails cleanly. Switching the Windows default output device mid-playback is still detected;
   closing the player ends promptly.
9. A CD+G file: seek past the end, before the start and to the middle clamp as before.
