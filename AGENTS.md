# Agent rules

FatzBomb ecosystem repo, id `fatzmedia`: FatzMedia.Audio in `Audio/` (was the FatzAudio repo, id
`fatzaudio`) and FatzMedia.Video in `Video/` (was FatzBombMedia, id `fatzvideo`). Ecosystem rules are in
the global CLAUDE.md; the map is `D:\development\FatzBomb\shared\ECOSYSTEM.md`.

- Each folder keeps its own rules: `Audio/AGENTS.md` and `Video/AGENTS.md`. Work in one folder at a time
  unless the task names both; a change that needs both lands in one branch and one PR.
- Output (test results, logs, scratch) goes in `D:\development\FatzBomb\work\fatzaudio\` or
  `work\fatzvideo\`, never in the repo.
- Video still uses Audio through the `FatzAudio` NuGet package (pinned), not by project reference.
- Workflows live in the root `.github/workflows`: Audio's run in `Audio/`, the `video-*` ones in `Video/`.
- Merging a PR here: squash, except a PR that brings in another repository's history (merge commit).
