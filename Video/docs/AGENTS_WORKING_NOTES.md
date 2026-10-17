# FatzBombMedia Working Notes

Use this file plus the single stage file named in the command.

Example intent:

```text
implement-stage docs/roadmap/active/<stage-file>.md
complete-stage docs/roadmap/active/<stage-file>.md
```

The named stage file is the only active work item. Other stage files are not active unless explicitly named.
- Active stages live in `docs/roadmap/active/` with the `ACTIVE` prefix.
- Future ideas live in `docs/roadmap/future/` with the `IDEA` prefix.
- Completed stages are archived in `docs/roadmap/complete/`.
- Implementation notes live in `docs/roadmap/updates/<stage>/`.

## Architecture Guardrail

Core owns playback, timing, composition policy, state, sidecars/backgrounds, CDG,
target fan-out, and renderer/platform contracts. Renderer packages own GPU resources,
swap chains, renderer-specific options, and final rendering execution. Platform packages
own OS integration and native-service registrations.

Adapters own only framework surface/control lifecycle and input interop.

Do not move media logic, renderer policy, layer roles, CDG handling, timing, or final presentation into WPF or any adapter.

## Working Style

- One focused change at a time.
- Do not redo completed work unless the current task directly invalidates it.
- Do not broaden scope unless the current code proves it is necessary.
- Future ideas are worthwhile without proving a current problem, but become active only
  when explicitly selected and bounded for implementation.
- If the stage is too large for one safe patch, complete a bounded part and write an update note.
- Validate proportional to risk.

## Validation Shortcut

- Docs/mapping only: no build; confirm no source edits.
- Small source edit: Debug build.
- Renderer/timing/public API/boundary/media behavior/threading/native lifetime: Debug + Release + boundary/parity scripts, plus targeted manual smoke for the touched behavior.
- Do not run the full validation checklist unless explicitly asked, closing a major stage, or preparing a release candidate.

## True Non-Goals

Do not plan or implement VLC-style plugin/module architecture, DVD/Blu-ray playback, or playlist/library ownership inside the media engine.

Audio CD and CD+G disc playback are in scope (owner decision, 2026-10-17); see `roadmap/future/032A_IDEA_AudioCdAndCdgDiscPlayback.md`. DVD and Blu-ray stay out.
