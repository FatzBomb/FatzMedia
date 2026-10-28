# Handoff

Updated: 2026-10-28. Branch `claude/wonderful-heisenberg-9x3p6a`. `main` holds everything through
PR #30. PR #31 (docs only: 045A complete, 047A kept, this file) is waiting for the owner to merge.

## State

- **046A adapter and player parity: complete** (`docs/roadmap/complete/046A_COMPLETED_AdapterParity.md`).
  The WPF, Avalonia and WinForms adapters and test players share their features:
  - Core: auto-hide, path reporting and the lightweight player controller;
  - every adapter: the second-screen window, `KaraokeSurface` and `BackgroundSurface`;
  - Player.Shared: the chrome controller, stats line, screen rule and renderer request;
  - every player: the GPU-first `--renderer=auto` and a toolbar for player count (1-4) and
    renderer.
  The WinForms seek bar is drawn by the player (`Player.WinForms/SeekBar.cs`).
- **045A GPU decoding on every path: complete** (`docs/roadmap/complete/045A_COMPLETED_GpuDecodingEveryPath.md`).
  - Direct3D11: D3D11VA on every surface.
  - Vulkan: Vulkan Video on the renderer's own device, Linux and Windows. FFmpeg must be built
    with Vulkan.
  - Avalonia: takes Vulkan frames as a shared GPU image on Windows (keyed-mutex Direct3D11
    textures).
  - Owner smoke passed on Windows (RTX 5090).
  - Not run: Linux drivers and the GPU-reset step (ENG-038).
- **047A WPF shared GPU surface: kept on the table** (`docs/roadmap/future/047A_IDEA_WpfSharedGpuSurface.md`).
  - What it is: WPF composited mode from `D3DImage` instead of a CPU copy, for 4K.
  - Done: the risk assessment (R1-R10; R1 frame hand-over without a keyed mutex and R5
    front-buffer loss decide it).
  - Next: the owner runs step 0 (CPU % and fps, native window vs `--surface=framesink`, 4K), then
    decides on the opt-in spike.
- Still active: 026A, 026B, 027A (WinUI 3 deferred), 033A, 041A, 042A, 043A, 044A.
  - 037A FatzAudio remains a future idea: replace BASS once proven.
  - All are listed in `docs/roadmap/STAGE_MANIFEST.md`.

## Decisions this session

- **Every player has the same features** (owner): no feature lives in only one player.
- **Full compatibility** (owner): every GPU renderer decodes on the GPU on every platform it runs
  on. Direct3D11 stays the Windows default.
- **Vulkan on Windows with ANGLE: Direct3D11 creates the shared texture and Vulkan imports it.**
  Direct3D11 cannot open Vulkan's opaque memory.
- **WPF and WinForms on Vulkan keep the frame sink.** Direct3D11 covers their native window, and a
  Vulkan swap chain is a later step.
- **Shared-image consumers call `CompleteImport` only after the compositor's imports finish.**
  This fixed fast resizes falling back to the frame sink.
- **The WinForms frame-sink surface paints each published frame with `Update()`.**
  `WM_PAINT` was starved during seek drags.

## How to verify

Linux sandbox:

```bash
EnableWindowsTargeting=true dotnet build FatzBomb.MediaControl.sln -c Release
dotnet test FatzBomb.MediaControl.Portable.Tests -c Release --no-build
FATZBOMB_LINUX_FFMPEG_DIR=/opt/fatzbomb-native/ffmpeg/lib FATZBOMB_LINUX_SAMPLE_VIDEO=/opt/fatzbomb-native/media/h264_1080p.mp4 \
  dotnet test FatzBomb.MediaControl.Linux.Tests -c Release --no-build
dotnet test FatzBomb.MediaControl.Rendering.Vulkan.Tests -c Release --no-build
dotnet test FatzBomb.MediaControl.Avalonia.Tests -c Release --no-build
EnableWindowsTargeting=true dotnet format FatzBomb.MediaControl.sln --verify-no-changes
for s in check-core-boundary check-adapter-boundary check-wpf-boundary check-presentation-parity; do
  DOTNET_ROOT=$(dirname $(readlink -f $(which dotnet))) ~/.dotnet/tools/pwsh -NoProfile -File scripts/$s.ps1; done
```

- Run `scripts/setup-linux-native.sh` first if `/opt/fatzbomb-native` is missing.
- Last results: Portable 1535, Linux 103, Vulkan 81, Avalonia 44 tests pass; 0 warnings.

Windows (owner):
- Repo at `C:\development\nuget\FatzbombMedia`, media in `C:\media`.
- Hardware: RTX 5090, three monitors.
- Smoke steps are in each stage's `docs/roadmap/updates/<stage>_<n>/StageUpdate.md`.
- The owner works with full PowerShell command sets (pull, run, what to look for) and prefers a
  dark UI.

## Next steps

1. **Code audit (next session; the owner asked for it).**
   - Look for bugs, performance and memory, redundant and reusable code, needless complication,
     over-engineering, and dead or orphaned code.
   - Plan:
     - a read-only audit by area: Core playback and pipeline; renderers (Direct3D11, Vulkan);
       the shared GPU image path; the adapters; the test players and Player.Shared; and a
       repo-wide dead-code sweep;
     - verify each finding against a real caller (XAML bindings, reflection and DI count as use);
     - one prioritised report for the owner;
     - fixes in separate batches (bugs, then dead code, then refactors), each validated with its
       own PR.
   - Known starting points:
     - ENG-037 and ENG-038;
     - the player rebuild loop, copied three times (WPF `MainWindow`, WinForms `MainForm`,
       Avalonia `MainWindow`);
     - `VideoDecoder`/coordinator downcasts to `RendererVideoDecodeServices`/`PipelineMediaEngine`;
     - the duplicated "hardware decoding (Vulkan Video)" status string;
     - the `Shared GPU images exported` Info line logged on every resize step (noisy).
2. **047A:** the owner's step 0 numbers, then a decision on the spike. It needs approval for a
   `Vortice.Direct3D9` package or a hand-written COM bridge.
3. Later: 037A FatzAudio; a Vulkan swap chain on the native window; the WinUI 3 adapter.

## Open questions for the owner

- 047A: step 0 results and go/no-go.
- 047A: if go, `Vortice.Direct3D9` (new package) or about six hand-written COM calls?
- WinForms overlay with smooth (per-pixel) transparency: wanted, or leave the colour-keyed overlay?

## Next-session prompt

> Read HANDOFF.md. Run the code audit described in its "Next steps" item 1: a read-only,
> area-by-area audit of FatzBomb.MediaControl for bugs, performance and memory, redundant and
> reusable code, needless complication, over-engineering, and dead or orphaned code. Verify every
> finding against a real caller before reporting it. Give me one prioritised report first. Do not
> change code until I pick what to fix, then fix in separate validated batches (bugs, dead code,
> refactors), each with its own PR. Keep 047A on the table: ask me for the step 0 numbers when the
> audit is done.
