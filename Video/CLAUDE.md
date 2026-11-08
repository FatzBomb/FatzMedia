@AGENTS.md

# Rules
Repo AGENTS.md wins on conflict. Mostly C#/.NET desktop (WPF, Avalonia, MVVM Toolkit); follow the
repo's stack and style.

## Code
- Smallest complete change; keep public APIs. Don't mix refactors with feature work.
- Modern language features where clearer; don't modernise untouched code.
- Reuse before writing new; extract on the third repeat. Propose a split when a file or class
  does two jobs; ~500 lines is a prompt to look, not a gate.
- Prove code is unused (incl. reflection, DI, XAML bindings) before deleting. Ask before adding
  dependencies.
- Out-of-scope issues you notice: list them at the end (or in the repo's notes file), don't fix
  them.

## Verify
- Once per change set, only what it could break; skip for docs/trivial edits. IDE diagnostics
  (when available) before a full build.
- Tests only for pure logic; for hardware, timing, native or UI, give a manual check.
- Never test on real user data or installed apps. Say what was and wasn't verified.

## Usage
Optimize for cost per finished task.
- Haiku: searches, logs. Sonnet: docs, tests, small fixes. Opus: DSP, codecs, threading, design,
  hard bugs, or a failed cheaper attempt. If Sonnet struggles, go to Opus medium, not Sonnet high.
- Effort: low mechanical, medium default, high for Opus design/debug; never max. Never Fable
  unless asked.
- Subagents for big searches (one Explore request) and independent work (sent together). Not for
  one known file or quick edits. Stuck on Sonnet: brief an Opus subagent.

## Workflow
- Commit/push only when asked or the repo's AGENTS.md grants standing approval.
- One task per session; if I start a second, suggest wrapping up first.
- Resuming: read HANDOFF.md (repo root or docs/) once if it exists.
- "Wrap up": update affected docs, create or rewrite HANDOFF.md (state, decisions, how to verify,
  next steps, open questions), ask before committing, end with a next-session prompt.
- Finished plan: summarize into one note, move originals to docs/archive/ (never delete; keep
  anything still referenced), trim memory.

## Pull requests
- Batch related steps into one PR. Build and run tests locally first.
- /code-review medium --fix once before opening (skip docs/trivial); re-run only after a large
  fix.
- Once open, avoid extra pushes. If CI fails, reproduce and fix locally, then push once. Wait for
  CI; don't poll.
