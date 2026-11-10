# FatzMedia.Video Docs

## Active files

- `../AGENTS.md` (repo root) — repo rules for agents: build, architecture, tests, workflow
- `roadmap/STAGE_MANIFEST.md` — completed milestones and future stage index

## Roadmap layout

```
roadmap/
  active/                 ← current stage only
  complete/               ← completed stage records
  future/                 ← good ideas with direction, immediate action, and non-goals
  STAGE_MANIFEST.md       ← stage index
  updates/                ← implementation notes when retained
```

Future idea files use the `IDEA` name. They are not problem-gated, but they remain
separate from active work until one is explicitly selected and given bounded acceptance
criteria in `roadmap/active/`.

## Other docs

- `site/` — the developer user guide and API reference (DocFX); build with `scripts/build-docs.ps1` or `.sh` (see `site/README.md`)
- `CURRENT_ARCHITECTURE.md` — ownership boundaries and runtime flow
- `VALIDATION.md` — manual smoke test checklist
- `LOCAL_VERIFY_PROMPT.md` — paste-ready agent prompt to clean, verify, and smoke-test a branch on Windows
- `NATIVE_DEPENDENCIES.md` — host-supplied FFmpeg distribution and update policy
- `PRIVATE_NATIVE_RUNNER.md` — secure self-hosted native smoke-runner provisioning
