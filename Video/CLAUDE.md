# Agent rules

The `Video\` folder of the FatzMedia repo: FatzMedia.Video, the video engine. See the root CLAUDE.md.

- Needs FatzMedia.Audio from `..\Audio` of this repo, by project reference; its native library comes
  from Audio's CMake build (`docs/NATIVE_DEPENDENCIES.md`).
- Output (test results, smoke-script results, logs, profiling, scratch) goes in
  `D:\development\FatzBomb\work\fatzmedia\Video\`, never in the repo. Legacy and design material lives in
  `D:\development\FatzBomb\assets\fatzmedia\Video` (read-only).

## FatzMedia.Video
.NET 10 media engine: UI-agnostic Core, renderer packages, platform packages, UI adapters, and
test hosts. Read `docs/CURRENT_ARCHITECTURE.md`, `docs/VALIDATION.md`,
`docs/NATIVE_DEPENDENCIES.md` and `docs/roadmap/STAGE_MANIFEST.md` when the task touches them,
not up front.

## Build
- `dotnet build FatzMedia.Video.sln`. Release treats warnings as errors. Shared settings
  live in `Directory.Build.props` (C# 14, nullable, implicit usings).
- Windows projects target x64 (`net10.0-windows7.0`); Core, the Software and Vulkan renderers,
  Linux and Avalonia are `net10.0`.
- No native binaries are tracked. To run a host, put the x64 FFmpeg DLLs in the host's
  `native/` folder: `FatzMedia.Video.Sample.Player`, `.Sample.Player.Avalonia` or `.Sample.Player.WinForms`.
  FatzAudio's library comes from Audio's build. Details are in `docs/NATIVE_DEPENDENCIES.md`.
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
- Audio is FatzAudio (FatzMedia.Audio, 037A); BASS is gone, so end users need no BASS DLLs or licence.
  Keep engine specifics behind `IAudioEngine` and its internal seams.
- Non-goals: plugin architecture, DVD/Blu-ray, playlist/library ownership. Audio CD and CD+G
  discs are in scope.
- File size is a prompt to look, not a rule. Split along real seams (a part that can be named
  and tested on its own; partial classes per concern, as `PlayerCore.*.cs` does), in its
  own commit, not mixed with feature work. Cohesive engine files (D3D11 composition, decoders,
  presentation, CDG) may stay large.

## Code rules
- Async engine code uses `.ConfigureAwait(false)`; locks use `System.Threading.Lock`.
- Host events (`MediaEnded`, `MediaStopped`, ...) fire on background threads; handlers marshal
  to the UI thread. Sync `new Player(...)`, `Pause()`, `Stop()` and `Dispose()` block; UI code
  uses `Player.CreateAsync` and the async control APIs.
- Diagnostics belong to the player that raised them (`DiagnosticRouter.EnterOwner`, an
  `AsyncLocal`). Work that drops the execution context (`ExecutionContext.SuppressFlow`,
  native callbacks) must re-enter the owner; process-wide work must not capture one.
- The WPF, Avalonia and WinForms test players bind one view model, `PlayerViewModel` in
  `FatzMedia.Video.Sample.Player.Shared` (CommunityToolkit.Mvvm, Core only; hosts supply UI thread,
  dialogs and engine through its `Hosting/` interfaces). Player logic goes there; hosts keep
  code-behind for view behaviour, surfaces and HWND interop.

## Tests and validation
- Test pure logic, plus player behaviour through the existing doubles (`TestPlatformFactory`,
  `TestMediaEngine`). Hardware, timing, native and UI paths get manual smoke steps instead.
- Keep portable tests in `FatzMedia.Video.Tests`; `Portable.Tests` compiles them by link.
  A Windows-only test file needs a `D3D11*`/`Direct3D11*` name or an entry in the `Exclude` list
  of `Portable.Tests`. Tests that mutate `FileTypes` use `[Collection("File type registry")]`.
- Windows CI is the source of truth for the full build and test run, the boundary scripts and
  format. WPF, D3D11 and native playback are verified manually (`docs/VALIDATION.md` and each
  stage's update note).

## Workflow
- One bounded stage at a time: move `docs/roadmap/future/*_IDEA_*` to `active/*_ACTIVE_*` with
  bounded acceptance criteria, write `docs/roadmap/updates/<stage>_<n>/StageUpdate.md` (decisions
  and manual smoke steps), and mark the manifest "implemented, pending manual smoke". Finished
  stages move to `complete/`. Bugs and deferred findings go in `docs/roadmap/ENGINEERING_ISSUES.md`.
