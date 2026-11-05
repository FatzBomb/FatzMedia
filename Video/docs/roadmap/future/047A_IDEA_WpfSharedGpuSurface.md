# Idea 047A - WPF Composited Surface from a Shared GPU Image

Prepared: 2026-10-27
Status: **planned, queue item 2 after 051A** (owner, 2026-11-05: the UI-thread cost matters for
every WPF host that animates over video, TriviaBomb and the karaoke app alike, and most shows run on
laptops). Earlier: not pursued (owner, 2026-10-28). The step 0 measurement found the WPF CPU copy costs
well under 1% of the CPU at 4K on the owner's machine, with no dropped frames, so the spike's risks
(R1, R5) are not worth taking. Results below. Kept here as the record; reopen only if a slower
machine or a 4K60 source shows real cost.
**Why it is planned again (2026-11-05).** Step 0 measured CPU % with 30 fps clips and an idle UI on a
desktop. The cost that matters is UI-thread time: each composited frame is written into the
`WriteableBitmap` on the UI thread (about 8 MB per 1080p frame, an estimated 1-2 ms), so two or three
1080p60 surfaces can take 3-5 ms of every 16.7 ms UI frame from the host's animations. On laptops
(shared memory, power limits) the copy costs more. This stage removes that per-frame UI-thread work.

**Guards decided (2026-11-05):**
- R1: `D3DImage.Lock()` blocks until WPF's render thread has copied the previous back buffer, so a
  texture of the ring (2-3) is reused only after a later successful lock; the producer waits for its
  GPU work (event query, off the UI thread) before announcing; `TryLock` with a short timeout skips a
  frame instead of blocking the UI.
- R5: `IsFrontBufferAvailableChanged` -> release and show the frame sink; on return recreate the
  Direct3D 9Ex device and re-import; sticky frame sink after repeated failures (as Avalonia).
- R7 (laptops, hybrid GPUs): create the renderer device on WPF's adapter, compare LUIDs, frame sink on
  a mismatch. R8: hand-written COM calls (about six), not `Vortice.Direct3D9`.

**Before and after numbers:** 051A adds a UI-thread time counter to `WpfCompositedSurfaceHost`; measure
2-3 composited 1080p60 surfaces under an animated WPF screen, on the desktop and on a laptop, before
and after.

**Slice 2 (owner 2026-11-05): composited video on its own thread.** Each composited surface hosts its
video element on a dedicated dispatcher thread (`HostVisual` in the main tree, `VisualTarget` on the
video thread): the per-frame work (the `D3DImage` hand-over, or the bitmap write) runs there, and WPF's
render thread composes both trees, so a busy or stalled app UI thread no longer pauses the video, while
WPF content above it, layout, clipping and opacity keep working. The A/B pattern (051A) still gives
same-frame switching (the new clip's first frame is shown before the host swaps). The surface tells the
hosted tree its size and DPI; device-loss events arrive on the video thread. Remaining differences from
the native window: WPF's composition beat (up to one frame later) and SDR only.

**Slice 3 (owner 2026-11-05): `WpfPresentationMode.Auto`.** Starts native, and switches a surface to
composited once, permanently (until the surface is recreated or the app sets a mode), when it sees
content that needs it: `OverlayContent` set, or the surface's own opacity, transform or clip. Decided
before the first frame when already known at load (no flash); a later first overlay may flash once.
Per surface. HDR media plays tone-mapped once a surface is composited. Content the app places over the
surface from elsewhere in its window cannot be detected: such apps set `WpfComposited`, and the docs
recommend `OverlayContent` for content over video. A read-only `EffectivePresentationMode` and a change
event report the mode in use. Whether `Auto` becomes the default is a separate decision after 047A.

**Venues run 4K displays (owner 2026-11-05).** A full-screen composited surface is read back at the
surface's size, not the source's: about 33 MB a frame at 4K, about 2 GB/s at 60 fps, whatever the clip's
resolution. That is the case slice 1 removes.

**What it does not change (slice 1 alone):** a UI-thread stall still pauses composited video (each frame needs a short
UI-thread call); WPF still shows frames on its own beat (up to one frame later than the native window);
SDR only. The native window stays the choice for HDR, exact frame timing, or a heavy UI thread with no
WPF content over the video. Composited is the choice whenever WPF content goes over the video.

## What it changes

WPF shows video in one of two modes (`WpfPresentationMode`):

- `NativeHwndOverlay` (default): the renderer presents into a child window. Fully GPU, HDR,
  exact frame timing. Unchanged by this idea.
- `WpfComposited`: WPF draws the video like any element, so WPF content, transforms, opacity
  and clipping work over it. Today the renderer reads each frame back to the CPU and the adapter
  copies it into a `WriteableBitmap` (`FatzBomb.MediaControl.Wpf/WpfCompositedSurfaceHost.cs`).
  At 4K that is about 33 MB per frame, about 2 GB/s at 60 fps, read back and uploaded again.

The idea: in `WpfComposited` with the Direct3D11 renderer, hand WPF the GPU texture through
`D3DImage` instead, as the Avalonia surface already does with its shared GPU image. The frame
sink stays as the fallback, and software and Vulkan renderers keep it.

## How it would fit the existing design

The Core contract already has what the WPF side needs:

- `MediaSharedGpuImageHandleKind.D3D11TextureGlobalSharedHandle` (legacy KMT handle: the only
  kind Direct3D 9Ex can open);
- `MediaSharedGpuImageSyncKinds.HostSynchronized` (no GPU primitive: the producer finishes its
  GPU work before announcing a frame; Direct3D 9 has no keyed mutex).

Work by layer:

- **Renderer (Direct3D11):** `D3D11SharedTextureBackend` offers legacy-shared plus keyed mutex
  today. Add a legacy-shared plus `HostSynchronized` variant: `ResourceOptionFlags.Shared`,
  B8G8R8A8 textures, and a GPU completion wait (event query) before a frame is announced.
- **Adapter (WPF):** a shared-image consumer like `AvaloniaGpuInterop`: a Direct3D 9Ex device on
  the renderer's adapter, `IDirect3DDevice9Ex::CreateTexture` with the shared handle, and
  `D3DImage.SetBackBuffer`, `AddDirtyRect` and `Unlock` per frame. The frame-sink host stays as
  the fallback. Opt-in at first (a `PreferGpuSharedSurface` property, the Avalonia name).
- **Scripts:** `check-wpf-boundary.ps1` and `check-core-boundary.ps1` must allow it on purpose.

## Risks

| # | Risk | Likelihood | Impact | Mitigation | How the spike checks it |
| --- | --- | --- | --- | --- | --- |
| R1 | **Synchronization without a keyed mutex.** Direct3D 9Ex cannot use DXGI keyed mutexes. WPF reads the texture on its own render thread some time after `Unlock`, and nothing tells the producer when it has finished. Rendering into a texture WPF is still reading gives torn or mixed frames. | Medium | High | A ring of at least 3 textures. The producer waits for its GPU work to finish before announcing a frame. A texture is reused only after WPF has shown a later one (`CompositionTarget.Rendering` after the switch). | 10 minutes of 4K playback plus fast scrubbing: no tearing or mixed frames. |
| R2 | **SDR only.** `D3DImage` takes 8-bit BGRA only. | Certain | Low | HDR stays on the native-window path; this path tone-maps to SDR, as the frame sink does today. Documented. | None needed. |
| R3 | **Frame timing.** WPF composes on its own schedule, so the engine's per-target cadence and present-time frame selection (017A) no longer decide when a frame shows. It can add up to one frame of latency and change A/V sync slightly. | Medium | Medium | Same exposure as the `WriteableBitmap` path today, which also shows frames when WPF composes. Measure A/V offset against the frame sink. | A/V offset within one frame of the frame sink; no new judder on 24p and 30p clips. |
| R4 | **UI-thread coupling.** `D3DImage` calls must run on the dispatcher, so a busy UI thread delays video. | Certain | Low | No regression: the `WriteableBitmap` path has the same coupling. The native-window path stays the choice for UI-heavy apps. | None beyond the frame-sink comparison. |
| R5 | **Front buffer and device loss.** Win+L, UAC prompts, display-mode changes, Remote Desktop and a GPU reset (TDR) take the front buffer away (`IsFrontBufferAvailableChanged`) or remove the device. Mishandled, this shows black or crashes. | Medium | High | On loss: release the back buffer and show the frame sink. On return: recreate the Direct3D 9Ex device and re-import. Renderer device removal already falls back on the Avalonia path (sticky frame sink). | Survives Win+L, a UAC prompt, a resolution change and an RDP session, each with a logged fallback and recovery. |
| R6 | **WPF software rendering.** Under RDP, `RenderOptions.ProcessRenderMode = SoftwareOnly` or a tier-0 GPU, `D3DImage` needs `enableSoftwareFallback` and copies through the CPU anyway. | Low | Low | Detect it and keep the frame sink (no gain, no harm). | Forced software mode logs the fallback. |
| R7 | **Different GPUs.** The Direct3D 9Ex device, the Direct3D11 renderer and WPF's composition must share one adapter. Moving the window to a monitor on another GPU (hybrid laptops, multi-GPU desktops) makes WPF copy between adapters. | Low (desktop) / Medium (laptop) | Medium | Check the LUID, as the Avalonia path does; on a mismatch use the frame sink. | Logged check; manual move between monitors on one GPU stays shared. |
| R8 | **New dependency and native interop.** Direct3D 9Ex needs `Vortice.Direct3D9` (a new package, owner approval) or about six hand-written COM calls in the WPF adapter. | Certain | Medium (process) | Decide the dependency before the spike. Keep renderer policy in the renderer; the adapter only imports. | Boundary scripts updated on purpose and passing. |
| R9 | **GPU memory.** At least 3 4K BGRA textures (about 100 MB) per composited surface, plus secondary surfaces. | Certain | Low | Acceptable on desktop GPUs; size the ring by surface. | Memory noted in the smoke. |
| R10 | **Benefit smaller than expected.** If the 4K copy is already cheap on modern hardware, the stage is not worth its risk. | Unknown | High (go/no-go) | Measure first (step 0). | Step 0. |

R1 and R5 decide the stage: both are about correctness, not speed.

Evidence for R10 (owner, 2026-10-27): the WPF CPU-copy path dropped frames in earlier use, and
moving to the native hardware path fixed them. The cost is real; step 0 measures how large it is
at 4K today, with hardware decoding (earlier drops may also have included software decoding).

## Step 0: measure today's cost (owner, no code)

On the owner's machine, WPF player, `C:\media\4k-hevc.mkv`, then a 1080p clip:

1. Default (native window): note CPU % and fps from the stats line after 30 s of playback.
2. `--surface=framesink` (WPF composited, `WriteableBitmap`): the same readings.
3. Optionally `--players=2` with both, to see how it scales.

The difference between 1 and 2 is the most this idea can save. A gap of a few percent of CPU at
4K means stop here; a large gap or dropped frames at 4K means run the spike.

### Step 0 results (owner, 2026-10-28)

WPF player, RTX 5090, 20 logical cores, `smoke-audit.ps1` test 1 (CPU measured by the script over
30 s; fps from the stats line; the clips are 30 fps):

| Run | CPU (of all cores) | GPU 3D | GPU video decode | fps | Stutter |
|---|---|---|---|---|---|
| 4K, native window | 1.2% (0.23 cores) | 4.1% | 19.4% | 27-30 | no |
| 4K, `--surface=framesink` | 1.5% (0.30 cores) | 10.4% | 21.7% | 29.5-30.7 | no |
| 1080p, native window | 0.6% (0.13 cores) | 1.7% | 2.8% | 29.5-30.7 | no |
| 1080p, `--surface=framesink` | 1.0% (0.20 cores) | 5.1% | 2.7% | 29.5-30.8 | no |
| 4K x2 players, native window | 1.0% (0.20 cores) | 9.9% | 39.6% | 29.5-30.7 | no |
| 4K x2 players, `--surface=framesink` | 1.8% (0.37 cores) | 13% | 24.6% | 29.5-30.7 | no |

The gap the spike could close is 0.3-0.8% of the CPU: "a few percent means stop here" (above).
The earlier drops (R10) were most likely software decoding, which 045A removed.

## Spike (slice 1, opt-in, time-boxed)

Smallest version that answers R1, R3 and R5:

- the renderer's `HostSynchronized` legacy-shared export;
- the WPF `D3DImage` consumer behind `PreferGpuSharedSurface` (default off), primary surface only;
- the player flag `--surface=gpushared` in the WPF player.

Go if:

- no tearing or mixed frames in 10 minutes of 4K playback and fast scrubbing (R1);
- A/V offset within one frame of the frame sink (R3);
- Win+L, a UAC prompt, a resolution change and an RDP session each fall back and recover (R5);
- CPU at 4K clearly below the frame sink from step 0 (R10).

No-go keeps the frame sink and records the measurements here.

## Not in this idea

- HDR through WPF composition (not possible with `D3DImage`).
- Vulkan or software renderers on this path (they keep the frame sink).
- Secondary and projector surfaces (after the spike, as Avalonia did in 027A slice 5).
- WinUI 3 (`SwapChainPanel` composes GPU swap chains natively; deferred in 027A slice 4).
