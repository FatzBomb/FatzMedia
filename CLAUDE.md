# FatzMedia

FatzBomb ecosystem repo, id `fatzmedia`: FatzMedia.Audio in `Audio/` (was the FatzAudio repo) and
FatzMedia.Video in `Video/` (was FatzBombMedia).

- Each folder keeps its own rules: `Audio/CLAUDE.md` and `Video/CLAUDE.md`. Work in one folder at a time
  unless the task names both; a change that needs both lands in one branch and one PR.
- Output (test results, logs, scratch) goes in `D:\development\FatzBomb\work\fatzmedia\Audio\` or
  `work\fatzmedia\Video\`, never in the repo.
- Video uses Audio's .NET binding (`FatzMedia.Audio`) by project reference; its native library comes
  from Audio's CMake build (`Audio\build\bin\Release` by default). `FatzMedia.sln` at the root opens both.
- The native library and C API keep the FatzAudio names (`fatzaudio.dll`, `fa_*`); the .NET names are
  `FatzMedia.Audio` and `FatzMedia.Video`.
- No Claude or Copilot review workflows in .github/workflows; review with /code-review before a PR.
- Workflows live in the root `.github/workflows`: Audio's run in `Audio/`, the `video-*` ones in `Video/`.
- Merging a PR here: squash, except a PR that brings in another repository's history (merge commit).
