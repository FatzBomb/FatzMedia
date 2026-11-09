# Agent rules

FatzBomb ecosystem repo, id `fatzaudio`. Ecosystem rules are in the global CLAUDE.md; the map is
`D:\development\FatzBomb\shared\ECOSYSTEM.md`.

- Don't add Claude or Copilot review workflows to .github/workflows/. Code review is done with
  /code-review before opening a PR.
- Docs are part of every feature: header comments, .NET XML docs (CS1591 is an error in the
  binding), a `docs/features` page with C and C# examples, and its rows in `docs/migration/bass.md`.
  The goal is that an app can drop every BASS DLL. The API should be intuitive, not BASS-like.
- The .NET package and assembly are `FatzMedia.Audio` (namespace `FatzMedia.Audio`); the native library
  and C API keep their names (`fatzaudio.dll` / `libfatzaudio`, `fa_*`). Never name the managed assembly
  `FatzAudio.dll`: it would collide with the native `fatzaudio.dll` on Windows.
- Build: the VC98 `cl.exe` on PATH must never be picked up; use the Visual Studio 18 2026 generator
  (`docs/building.md`).
- Clean-room: never unpack or reverse BASS internals; compare only through `tools/bass-compare`.
