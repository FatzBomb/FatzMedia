# Idea 047A - WPF Composited Surface from a Shared GPU Image

Prepared: 2026-10-27
Status: idea. Owner direction (2026-10-27): "We should allow the WPF savings at 4K. We need to
establish the risks first." This file is the risk assessment and the measurement and spike that
decide whether the stage goes ahead.

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
