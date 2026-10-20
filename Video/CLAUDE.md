# CLAUDE.md

FatzBomb.MediaControl — .NET 10 media engine (UI-agnostic Core, renderer packages,
platform packages, UI adapters). `AGENTS.md` holds the full repo rules; this file is
the short orientation for Claude sessions.

## Read first

- `README.md`, `AGENTS.md`
- `docs/AGENTS_WORKING_NOTES.md` — working style, architecture guardrail, non-goals
- `docs/CURRENT_ARCHITECTURE.md` — ownership boundaries and runtime flow
- `docs/VALIDATION.md` — manual smoke checklist
- `docs/roadmap/STAGE_MANIFEST.md` — stage order; `docs/roadmap/ENGINEERING_ISSUES.md`

## Architecture guardrail

Core owns playback, timing, composition policy, state, sidecars/backgrounds, CDG,
target fan-out, and renderer/platform contracts. Renderer packages own GPU resources,
swap chains, renderer options, and final rendering. Platform packages own OS
integration and native-service registration. Adapters own only framework
surface/control lifecycle and input interop. Never move media logic, renderer policy,
layer roles, CDG, timing, or presentation into WPF or any adapter. Core must not
reference Win32/Vortice/WPF/renderer/platform projects. Reference DAG is pinned in
`scripts/check-core-boundary.ps1` — update it deliberately when adding projects.

True non-goals: VLC-style plugin/module architecture, DVD/Blu-ray playback,
playlist/library ownership inside the engine. Audio CD and CD+G disc playback are in
scope (future stage 032A).

## Working style

One bounded stage at a time: move `future/*_IDEA_*` to `active/*_ACTIVE_*`, bounded
acceptance criteria, focused xUnit tests for pure logic, write
`docs/roadmap/updates/<stage>_<n>/StageUpdate.md` (including manual smoke steps and
decisions made), mark the manifest "implemented, pending manual smoke". One commit
per stage/slice. Release builds treat warnings as errors — write warning-clean code.

Usage (owner rule): run helper agents (searches, code exploration, reviews, other
subagent tasks) on Sonnet whenever Sonnet can do the task, to save usage. Only when it
actually saves: keep the main model for work that needs it (hard debugging, timing or
rendering design) and for forked copies of the main agent, which always use it. Never use
Fable (the most expensive and usage-limited model) unless the owner asks for it: Opus 5.5 is
the main model for hard work and forks, Sonnet or Haiku for helpers.

## Validation split

- **Linux sandbox:** `dotnet build -c Release` of the portable `net10.0` projects and,
  with `EnableWindowsTargeting=true` (env var or `-p:`), the whole solution. The `net10.0`
  test projects run here: `Portable.Tests`, `Linux.Tests`, `Rendering.Vulkan.Tests`
  (needs Mesa lavapipe: `apt-get install mesa-vulkan-drivers`), `Avalonia.Tests`
  (headless). `Portable.Tests` compiles every file in `FatzBomb.MediaControl.Tests` by link,
  except the Windows-only files named in its csproj `Exclude`: keep portable tests in
  `FatzBomb.MediaControl.Tests` (they run in both projects, Windows and Linux CI) and do not
  move them; a new Windows-only test file needs a `D3D11*`/`Direct3D11*` name or an entry
  in that `Exclude` list. `dotnet format FatzBomb.MediaControl.sln --verify-no-changes` works with
  `EnableWindowsTargeting=true`. Boundary scripts run with the PowerShell .NET tool
  (`dotnet tool install --global PowerShell`, then `DOTNET_ROOT=<sdk root> pwsh -File
  scripts/check-*.ps1`). The Windows test project, WPF, D3D11, and BASS playback cannot
  run here.
- **Windows CI (`windows-latest`):** source of truth for the full build, the main
  test project, boundary scripts (`scripts/check-*.ps1`), presentation parity, and
  `dotnet format --verify-no-changes`. **Linux CI (`ubuntu-latest`)** runs the portable,
  Linux, Vulkan (lavapipe), and Avalonia (headless) test projects.
- **Manual:** native-DLL smoke on Windows per `docs/VALIDATION.md` and each stage's
  update note.
