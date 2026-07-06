# FatzBombMedia Command-Driven Stage System

This package is organized for a simple workflow:

```text
implement-stage stages/<stage-file>.md
complete-stage stages/<stage-file>.md
```

Normal agent context should be only:

```text
AGENTS_WORKING_NOTES.md
stages/<the named stage file>.md
```

Do not send every stage file. Do not send completed history unless the current stage needs it.

## Layout

```text
AGENTS_WORKING_NOTES.md
stages/
  003B_ACTIVE_MediaStateOwnershipMap.md
  004A_BACKLOG_ErrorSurface.md
  ...
  022A_FUTURE_ThreadModelProfilingGate.md
  completed/
  updates/
```

- `ACTIVE` is the current recommended stage.
- `BACKLOG` stages are accepted near-term work.
- `FUTURE` stages are wanted later work with distilled intent, risks, and first safe step.
- `stages/updates/` is for temporary notes while a stage is being implemented.
- `stages/completed/` is for cleaned completed summaries.

## Update note convention

If a stage requires multiple passes, write a small update note such as:

```text
stages/updates/004A_ErrorSurface_patch_01.md
```

When the stage is complete, summarize the stage file plus update notes into a completed file and move/archive the original stage.
