# FatzBomb.MediaControl

.NET 10 media engine: UI-agnostic Core, renderer packages, platform packages, UI adapters, and
test hosts. Read `docs/CURRENT_ARCHITECTURE.md`, `docs/VALIDATION.md`,
`docs/NATIVE_DEPENDENCIES.md` and `docs/roadmap/STAGE_MANIFEST.md` when the task touches them,
not up front.

## Build
- `dotnet build FatzBomb.MediaControl.sln`. Release treats warnings as errors. Shared settings
  live in `Directory.Build.props` (C# 14, nullable, implicit usings).
- Windows projects target x64 (`net10.0-windows7.0`); Core, the Software and Vulkan renderers,
  Linux and Avalonia are `net10.0`.
- No native binaries are tracked. To run a host, put the x64 BASS and FFmpeg DLLs in the host's
  `native/` folder: `FatzBomb.MediaControl.Player`, `.Player.Avalonia` or `.Player.WinForms`.
  Details are in `docs/NATIVE_DEPENDENCIES.md`.
- After editing Vulkan `Shaders/*.vert|*.frag`, run `scripts/compile-vulkan-shaders.sh` and
  commit the `.spv` files; CI checks them.

## Architecture (most important invariant)
- **Core** owns playback, timing, composition policy, state, sidecars/backgrounds, CDG, player
  modes, target fan-out and the renderer/platform contracts. It must not reference Win32,
  Vortice, WPF, renderer or platform projects, and it has no `[DllImport]`/`[LibraryImport]`.
- **Renderers** (Direct3D11, Software, Vulkan) own GPU resources, swap chains, shared-image
  export and final rendering. **Platform packages** (Windows, Linux) own OS integration and
  native loading. **Adapters** (WPF, Avalonia, WinForms) own only surface/control lifecycle and
  input interop: never media logic, renderer policy, CDG, timing or presentation.
- The project reference DAG is pinned in `scripts/check-core-boundary.ps1`; update it on
  purpose when adding a project or reference. `check-adapter-boundary.ps1`,
  `check-wpf-boundary.ps1` and `check-presentation-parity.ps1` guard the rest.
- Audio is BASS today. FatzAudio is planned as an opt-in second engine behind `IAudioEngine`,
  with BASS kept as the default and fallback (`docs/roadmap/future/037A_IDEA_FatzAudioEngine.md`).
  Keep BASS specifics behind that interface.
- Non-goals: plugin architecture, DVD/Blu-ray, playlist/library ownership. Audio CD and CD+G
  discs are in scope.
- Large engine files (D3D11 composition, decoders, presentation, CDG) are expected; don't split
  them on line count. Keep new UI and glue code small.

## Code rules
- Async engine code uses `.ConfigureAwait(false)`; locks use `System.Threading.Lock`.
- Host events (`MediaEnded`, `MediaStopped`, ...) fire on background threads; handlers marshal
  to the UI thread. Sync `new Player(...)`, `Pause()`, `Stop()` and `Dispose()` block; UI code
  uses `Player.CreateAsync` and the async control APIs.
- Diagnostics belong to the player that raised them (`DiagnosticRouter.EnterOwner`, an
  `AsyncLocal`). Work that drops the execution context (`ExecutionContext.SuppressFlow`,
  native callbacks) must re-enter the owner; process-wide work must not capture one.
- The WPF `Player` host uses MVVM (CommunityToolkit.Mvvm); code-behind only for view behaviour
  and HWND interop. The Avalonia and WinForms test players are plain code-behind.

## Tests and validation
- Test pure logic, plus player behaviour through the existing doubles (`TestPlatformFactory`,
  `TestMediaEngine`). Hardware, timing, native and UI paths get manual smoke steps instead.
- Keep portable tests in `FatzBomb.MediaControl.Tests`; `Portable.Tests` compiles them by link.
  A Windows-only test file needs a `D3D11*`/`Direct3D11*` name or an entry in the `Exclude` list
  of `Portable.Tests`. Tests that mutate `FileTypes` use `[Collection("File type registry")]`.
- Linux sandbox: build with `EnableWindowsTargeting=true`; run `Portable.Tests`, `Linux.Tests`,
  `Rendering.Vulkan.Tests` (needs `mesa-vulkan-drivers`) and `Avalonia.Tests` (headless).
  `dotnet format FatzBomb.MediaControl.sln --verify-no-changes` works with
  `EnableWindowsTargeting=true`. Run the boundary scripts with the PowerShell dotnet tool:
  `dotnet tool install --global PowerShell`, then
  `DOTNET_ROOT=$(dirname $(readlink -f $(which dotnet))) ~/.dotnet/tools/pwsh -File scripts/<script>.ps1`.
- Windows CI is the source of truth for the full build and test run, the boundary scripts and
  format. WPF, D3D11 and native playback are verified manually (`docs/VALIDATION.md` and each
  stage's update note).

## Workflow
- One bounded stage at a time: move `docs/roadmap/future/*_IDEA_*` to `active/*_ACTIVE_*` with
  bounded acceptance criteria, write `docs/roadmap/updates/<stage>_<n>/StageUpdate.md` (decisions
  and manual smoke steps), and mark the manifest "implemented, pending manual smoke". Finished
  stages move to `complete/`. Bugs and deferred findings go in `docs/roadmap/ENGINEERING_ISSUES.md`.
- One commit per stage or slice.
- Standing approval: commit and push to the session's working branch. Open a PR, merge, or
  push to `main` only when asked.
