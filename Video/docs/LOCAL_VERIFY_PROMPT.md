# Local Verification Prompt

A prompt for a coding agent (for example Claude Code) running **on your own Windows PC**
inside this repository. It cleans leftover tool artifacts, switches to a branch, runs
every automated check, fixes and pushes failures to that branch, and then walks you
through the manual smoke checklist.

Before pasting it:

- Replace `<repo path>` with your local clone, for example `C:\src\FatzMedia.Video`.
- Replace `<branch>` with the branch to verify, for example
  `claude/wonderful-heisenberg-9x3p6a`.
- Replace `<PR link>` with the branch's pull request, whose description holds the
  manual smoke checklist (otherwise the agent uses `docs/VALIDATION.md`).

The agent stops for your confirmation before deleting anything and when it finds
uncommitted changes. It never pushes to `main` or rewrites history.

```text
You are working in my local FatzMedia.Video repo on Windows (PowerShell), at:
  <repo path>

Goal: clean this folder of leftover tool artifacts, switch to the branch
<branch>, verify it builds and passes all checks, fix anything that fails,
and push fixes to that same branch.

Rules:
- Never push to main. Never force-push, rebase, or rewrite history.
- Never delete anything before showing me the list and getting my "yes".
- Keep the FFmpeg DLLs in FatzMedia.Video.Sample.Player\native\ safe at all times.
- Read CLAUDE.md, AGENTS.md and docs/VALIDATION.md before changing code; follow them.
- Stop and ask me if something is ambiguous or a fix would change behaviour.

Steps:
1. Run `git status`. If there are uncommitted changes to tracked files, show
   them and ask me what to do before continuing.
2. Back up FatzMedia.Video.Sample.Player\native\*.dll to C:\FatzBomb-native-backup\
   and confirm the files are there.
3. Preview cleanup:
     git clean -ndx -e "FatzMedia.Video.Sample.Player/native/*.dll"
   Show me the list, grouped (tool leftovers, bin/obj build output, other).
   Point out anything that looks like real work I might want to keep.
   Wait for my "yes", then run the same command with -fdx instead of -ndx.
4. git fetch origin
   git switch <branch>
   git pull origin <branch>
   If the native DLLs are missing afterwards, restore them from the backup.
5. Check the toolchain: `dotnet --version` must satisfy global.json (10.0.302 or a
   newer 10.0 feature band). If PowerShell 7 (`pwsh`) is missing, tell me how to
   install it rather than skipping the boundary scripts.
6. Verify, in this order, and record each result:
     dotnet restore FatzMedia.Video.sln
     dotnet build -c Release FatzMedia.Video.sln --no-restore
     dotnet test -c Release --no-build
     dotnet format FatzMedia.Video.sln --verify-no-changes
     pwsh scripts/check-core-boundary.ps1
     pwsh scripts/check-wpf-boundary.ps1
     pwsh scripts/check-adapter-boundary.ps1
     pwsh scripts/check-presentation-parity.ps1
7. If anything fails: find the root cause, make the smallest fix, re-run the
   failing check and then the full list above. Never skip, disable or weaken a
   test to make it pass. Commit each fix with a clear message and push with
     git push origin <branch>
8. Report: a table of every check (pass/fail), what you fixed and pushed, and
   anything you could not fix with the exact error.
9. Then help me with the manual smoke checklist in <PR link> (or
   docs/VALIDATION.md): start the Player for each item, e.g.
     dotnet run --project FatzMedia.Video.Sample.Player -c Release -- <flags> <file>
   ask me what I see and hear, and keep a running pass/fail list. If I report a
   bug, investigate, fix, re-verify (step 6) and push, then ask me to re-test.
   Ask me for media file paths when you need them.
```
