# Stage Update

Stage: 045A.3 - Vulkan on Windows: GPU decoding and the Avalonia shared GPU image
Date: 2026-10-27

## Status: COMPLETE - owner smoke passed 2026-10-26/27 (Windows)

Owner decision (2026-10-27): full compatibility. Slice 2 (`updates/045A_2`) gave the Vulkan
renderer GPU decoding: FFmpeg Vulkan Video on the renderer's own device. The same code runs on
Windows. This slice adds the Windows edges:

- the Avalonia surface takes Vulkan frames as a shared GPU image instead of a frame-sink copy
  (the 027A slice 2 deferral);
- the WPF and WinForms players decode on the GPU with `--renderer=vulkan` and keep their frame
  sink.

The smoke steps below cover both slices. The owner's RTX 5090 is the first machine with a
video decode queue to run them on.

## Delivered

- **Decoding on Windows.** Nothing Windows-specific was needed in Core or the platform package.
  The Vulkan renderer's backend supplies its device to every decoder (slice 2), and FFmpeg's
  Windows build must include Vulkan. If it does not, the log says so in one line (see
  Fallbacks). Direct3D11 stays the Windows default; `--renderer=auto` is unchanged.
- **WPF and WinForms players** (`PlayerRendererSelection`, `--renderer=vulkan`):
  - they decode with hardware when the Vulkan probe allows it, and show the frames through
    their frame sink (GPU decode and conversion, one readback);
  - the log line reads `Vulkan forced (--renderer=vulkan): <GPU> (DiscreteGpu), Vulkan Video
    decoding: H.264, HEVC, AV1...; shown through frame sinks, with hardware decoding (Vulkan
    Video) where the GPU supports the codec`;
  - a Vulkan swap chain on the native window is still a later step.
- **Avalonia shared GPU image on Windows** (`VulkanOffscreenCompositor.Win32Export.cs`). The
  renderer offers both forms Avalonia's compositors import and lets the request choose:
  - **Keyed-mutex Direct3D11 textures** (`D3D11TextureNtHandle`, `KeyedMutex`). This is what
    the default ANGLE/Direct3D11 compositor imports.
    - Direct3D11 creates the shared texture: RGBA8, render target and shader resource,
      `SHARED_NTHANDLE | SHARED_KEYEDMUTEX`. It does so on the adapter of the Vulkan device,
      found by LUID (`VulkanD3D11Interop`: plain COM through vtables, no new package).
    - Vulkan imports the texture as the exported image's memory (`VK_KHR_external_memory_win32`,
      handle type `D3D11_TEXTURE`, dedicated allocation).
    - Each frame's submission acquires the image's keyed mutex with the ring's key and releases
      `ConsumerKey` (`VK_KHR_win32_keyed_mutex`, 500 ms timeout).
    - The blit takes the image from `VK_QUEUE_FAMILY_EXTERNAL` and hands it back in `GENERAL`
      layout. ANGLE then opens the same NT handle through the existing keyed-mutex consumer
      path, unchanged.
  - **Opaque NT handles** (`VulkanOpaqueNtHandle`), optionally with exported NT-handle
    semaphores (`VK_KHR_external_semaphore_win32`), for Avalonia's Vulkan backend. This is the
    Linux opaque-descriptor export with NT handles.
  - NT handles stay with the renderer, because importers open their own references. They are
    closed when both sides are done with the set; Linux descriptors are still handed to their
    importer.
  - The LUID check (`MatchesDevice`) declines a compositor on another GPU, and the surface uses
    its frame sink with the reason. So do any export failure and a device without either path.

## Decisions and deviations from the plan

- **Direct3D11 creates the shared texture and Vulkan imports it.** The plan read "export the
  Vulkan images as `VulkanOpaqueNtHandle` ... open the NT handle as a D3D11 texture". Direct3D11
  cannot open Vulkan's opaque memory handles, and Vulkan drivers do not export the `D3D11_TEXTURE`
  handle type. The supported direction is the reverse: a DXGI keyed-mutex texture imported into
  Vulkan. It reaches the same consumer path. The texture is created through COM vtables, so the
  renderer stays `net10.0` with no Windows-only package. Project references are unchanged.
- **Ownership transfer only for the Direct3D11 textures.** The opaque NT-handle images keep the
  Linux behaviour: they are left in `TRANSFER_SRC_OPTIMAL` and the layout is announced to the
  importer. That path works with Avalonia's Vulkan backend on Linux.
- **The test player does not exercise Avalonia's Vulkan backend on Windows.** It uses Avalonia's
  default (ANGLE). The opaque NT-handle path is offered and unit-tested for negotiation only
  (ENG-038).

## Fallbacks (one line each)

| Missing piece | Line |
|---|---|
| FFmpeg built without Vulkan | `VideoDecoder: Hardware decoding on the renderer's device is unavailable (this FFmpeg build has no Vulkan hardware context (it was built without --enable-vulkan)); decoding in software.` |
| Driver without Vulkan Video, or a missing feature | `Vulkan: Vulkan device: <GPU> (...), no Vulkan Video decoding: <reason>` and the renderer line `software decoding (...)` |
| Compositor on another GPU | `Shared GPU images unavailable for 'primary': ... LUID ...`; the surface logs its frame-sink reason |
| Direct3D11 device or texture creation failed | `Shared GPU images unavailable for 'primary': exporting the shared images failed: <reason>` |
| Neither export path on the device | `Shared GPU images unavailable ...: the Vulkan device can neither export ... nor import keyed-mutex Direct3D11 textures (...)` |

## Verification (Linux sandbox)

- Release build (with `EnableWindowsTargeting=true`, which compiles every Windows path): 0
  warnings, 0 errors.
- Tests: Portable 1535, Linux 103, Vulkan 81, Avalonia headless 44, all passing.
- `dotnet format --verify-no-changes` clean; the four boundary scripts and
  `check-vulkan-shaders.sh` pass.
- New tests (`VulkanSharedImageTests`):
  - `WindowsExport_MeetsAngleWithKeyedMutexTextures_AndAvaloniasVulkanBackendWithNtHandles`:
    - the capabilities a Windows device with both paths offers;
    - an ANGLE-like request selects `D3D11TextureNtHandle` with the keyed mutex;
    - a Vulkan-backend request selects `VulkanOpaqueNtHandle` with NT-handle semaphores;
    - without the keyed-mutex path, ANGLE is told why.
  - `ExportBuffers_CarryTheHandlesOfTheirKind`: the buffer handles for each kind.
- The Linux export (opaque descriptors) is unchanged, and its tests pass on lavapipe.
- **Not verified:**
  - anything that runs only on Windows: the COM interop, the Direct3D11 texture import, the
    keyed-mutex submit, ANGLE opening the texture, NT-handle closing;
  - Vulkan Video decoding itself, on any OS.
  The smoke steps below are the check.

## Manual smoke (Windows, RTX 5090)

From `C:\development\nuget\FatzbombMedia` in PowerShell. The x64 FFmpeg 9 DLLs (a build **with
Vulkan**, for example a Gyan or BtbN 9.0 shared build) and BASS go in each player's `native\`
folder. Logs: `$env:TEMP\FatzBombMedia\player-avalonia-debug.log`, `player-debug.log` (WPF) and
`player-winforms-debug.log` (WinForms).

Base commands:

```powershell
dotnet run --project FatzBomb.MediaControl.Player.Avalonia -c Release -f net10.0-windows7.0 -- --renderer=vulkan C:\media\video.mp4
dotnet run --project FatzBomb.MediaControl.Player -c Release -- --renderer=vulkan C:\media\video.mp4
dotnet run --project FatzBomb.MediaControl.Player.WinForms -c Release -- --renderer=vulkan C:\media\video.mp4
```

1. **Vulkan Video on the device (Avalonia).** Run the Avalonia command.
   - The log shows `Vulkan: Vulkan device: NVIDIA GeForce RTX 5090 (DiscreteGpu), Vulkan Video
     decoding: H.264, HEVC, AV1` (VP9 too if the driver reports it).
   - The renderer line reads `renderer Vulkan: Vulkan composition (NVIDIA GeForce RTX 5090
     (DiscreteGpu), Vulkan Video decoding: ...), hardware decoding (Vulkan Video) where the GPU
     supports the codec`.
   - **GPU decode is proven by** `VideoDecoder: Decoding H264 ... decoder h264 via vulkan`. Also
     check: no `Hardware decoding on the renderer's device is unavailable` line, and no
     `in software` on the `Decoding` line.
   - If the log instead says `this FFmpeg build has no Vulkan hardware context`, swap the
     FFmpeg DLLs for a build with Vulkan and repeat.
2. **Shared GPU image (Avalonia).**
   - **The shared path is proven by** `Vulkan: Shared GPU images exported for 'primary':
     ...D3D11TextureNtHandle, KeyedMutex...`, then `Avalonia.MediaSurface: Shared GPU images
     imported: WxH Rgba8Unorm x2, D3D11TextureNtHandle, KeyedMutex`, and a status line ending in
     `via shared GPU image`.
   - Play 1 minute with seeks, pause and resume: no black frames, no flicker, no tearing, no
     `Vulkan.DeviceLost`, no `A decoded video frame cannot be drawn`.
   - If the status line ends `via frame sink`, note the logged reason (LUID, export failure).
3. **Resize and second screen (Avalonia).** Maximize, restore and drag the edge while playing
   and while paused. A new `Shared GPU images exported` line follows each size, and the picture
   follows. Open the second screen: both screens show the video, and the status adds `2nd screen
   WxH via shared GPU image`. Close it: the main screen keeps playing.
4. **CPU.** Play `C:\media\video.mp4` for 30 s as in step 1, then again with
   `--decoding=software`. The second run logs `decoder h264 in software` and its renderer line
   ends `software decoding (--decoding=software)`. Note the status line's CPU % for each; the
   first should be clearly lower. Repeat for `C:\media\4k-hevc.mkv`: `decoder hevc via vulkan`,
   smooth playback, no dropped-frame bursts in the `VideoPipeline:Perf` lines.
5. **HDR.** Run `C:\media\hdr10.mp4`, then `C:\media\hlg.mp4`.
   - Both log `via vulkan`.
   - The picture matches `--renderer=direct3d11` on the same clip (the shader is a port of the
     Direct3D11 tone-map shader): not washed out, highlights not clipped.
   - `--hdr-tonemap-cpu` logs CPU tone mapping and looks the same.
6. **Next media.** While a clip plays, open another. The log shows `via vulkan` again, with at
   most one black frame at the switch.
7. **Two players (Avalonia).** Run with `--players=2` and open a video in each. Both log `via
   vulkan` and `via shared GPU image` and play smoothly. Closing one does not disturb the other.
8. **Renderer switching (Avalonia).** Switch the toolbar to Direct3D11 and back to Vulkan while
   a clip is loaded. Direct3D11 logs `via d3d11va`, Vulkan `via vulkan`, and both reach `via
   shared GPU image`.
9. **WPF.** Run the WPF command.
   - The log shows `Vulkan forced (--renderer=vulkan): NVIDIA GeForce RTX 5090 (DiscreteGpu),
     Vulkan Video decoding: ...; shown through frame sinks, with hardware decoding (Vulkan Video)
     where the GPU supports the codec` and `decoder h264 via vulkan`.
   - Play, seek and pause. Repeat with `C:\media\hdr10.mp4`.
   - Compare the CPU against `--decoding=software`.
10. **WinForms.** As step 9, with the WinForms command and its log.
11. **Fallback check (optional).** Run step 1 with `--decoding=software`: no `via vulkan`, the
    renderer line ends `software decoding (--decoding=software)`, and the picture still uses
    `via shared GPU image`.
12. **GPU reset (optional, Graphics Tools feature).** During Avalonia playback, run
    `dxcap -forcetdr` from an elevated prompt. Expected: `Vulkan.DeviceLost`, then
    `Decoder.DeviceLost` with software decoding, then `Hardware video decode restored` within
    seconds, and the picture back on `via shared GPU image`. If it stays black, note the log
    (ENG-038, frames from a replaced device).

## Owner smoke results (2026-10-27, Windows, RTX 5090)

- Steps 1, 2, 4 to 10 passed:
  - Vulkan Video decodes H.264, HEVC (8- and 10-bit, 4K), AV1 and HDR10 (tone mapped) in the
    Avalonia, WPF and WinForms players (`via vulkan`);
  - Avalonia shows them through keyed-mutex Direct3D11 textures (`via shared GPU image`),
    with two players at once.
  - One `frame dropped` line at start-up, as on the Direct3D11 path.
- Step 3 failed: after two quick resizes (two sets exported about 20 ms apart) the surface logged
  `the compositor could not import a shared image: OpenSharedResource1 failed` and stayed on the
  frame sink. The same happened on the second screen, and with `--decoding=software` (step 4), so
  it is the export hand-over, not decoding.
  - Cause: Avalonia's `ImportImage` returns before the compositor opens the handle on its own
    thread, but the surface reported `CompleteImport` at once. When a newer set retired the
    previous one, the renderer closed that set's NT handles before the compositor had opened
    them. The failure of an already replaced set was then treated as final.
  - Fix (`AvaloniaSharedImageSurface`): `CompleteImport` runs only when every import of the set
    has finished, so the renderer keeps the handles open until then; and an import failure of a
    set a newer one replaced is ignored. The same consumer serves the Direct3D11 renderer, which
    gets the same protection.
  - Retest: step 3 (drag the window edge quickly, maximize and restore, the second screen): the
    status stays `via shared GPU image`.
- Step 3 retest after the fix: over 130 re-exports during fast resizes of the main window and
  the second screen, and the surface stayed `via shared GPU image`. Passed.
- WinForms with two players, Vulkan and software renderers (the frame-sink path), not Direct3D11:
  seeking one player stalled the other, and the seeking player was slow when a seek started.
  - Cause: the WinForms surface published each frame with `Invalidate()`, and Windows sends
    `WM_PAINT` only when no other message waits. A seek drag's mouse moves and both players'
    frame publishes kept the UI thread's queue busy, so paints were postponed. Direct3D11
    presents into the child window and never needs the UI thread.
  - Fix (`WinForms/MediaSurface.PublishFrame`): `Update()` after `Invalidate()` paints the
    published frame at once. Publishes stay coalesced (latest frame wins).
  - Retest: `--players=2 --renderer=vulkan` (and `--renderer=software`), play both, drag one
    seek bar: the other keeps playing smoothly and the dragged one follows the drag.
  - Retest passed (2026-10-27): HEVC 10-bit and AV1 via Vulkan, and H.264 and AV1 on the
    software renderer, two players, smooth while one seeks.
- Not run: the optional GPU-reset step (12).
