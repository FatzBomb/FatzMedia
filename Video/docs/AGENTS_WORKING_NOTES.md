# FatzBombMedia Working Notes

Use this file plus the single stage file named in the command.

Example intent:

```text
implement-stage stages/004A_BACKLOG_ErrorSurface.md
complete-stage stages/004A_BACKLOG_ErrorSurface.md
```

The named stage file is the only active work item. Other stage files are not active unless explicitly named.

## Architecture Guardrail

Core owns media, timing, rendering, composition, state, sidecars/backgrounds, CDG, swap chains, target fan-out, and final presentation.

Adapters own only framework/HWND/control interop.

Do not move media logic, renderer policy, layer roles, CDG handling, timing, or final presentation into WPF or any adapter.

## Working Style

- One focused change at a time.
- Do not redo completed work unless the current task directly invalidates it.
- Do not broaden scope unless the current code proves it is necessary.
- Future stages are not active unless explicitly named/promoted.
- If the stage is too large for one safe patch, complete a bounded part and write an update note.
- Validate proportional to risk.

## Validation Shortcut

- Docs/mapping only: no build; confirm no source edits.
- Small source edit: Debug build.
- Renderer/timing/public API/boundary/media behavior/threading/native lifetime: Debug + Release + boundary/parity scripts, plus targeted manual smoke for the touched behavior.
- Do not run the full validation checklist unless explicitly asked, closing a major stage, or preparing a release candidate.

## True Non-Goals

Do not plan or implement VLC-style plugin/module architecture, Disc/DVD/Blu-ray playback, or playlist/library ownership inside the media engine.
