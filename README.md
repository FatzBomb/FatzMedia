# FatzMedia

Media engines by FatzBomb Entertainment. Two products in one repository, each built,
versioned and shipped as its own packages:

| Folder | Product | What it is | Packages |
|---|---|---|---|
| [`Audio/`](Audio/README.md) | FatzMedia.Audio (was FatzAudio) | Cross-platform audio engine: native C library (`fatzaudio`) with a .NET binding. Needs nothing else. | `FatzMedia.Audio` |
| [`Video/`](Video/README.md) | FatzMedia.Video (was FatzBomb.MediaControl) | .NET media engine for playback with a picture: video, A/V timing, karaoke presentation, renderers and UI adapters. Plays audio through FatzMedia.Audio. | `FatzMedia.Video`, `FatzMedia.Video.*` |

An audio-only app references only `FatzMedia.Audio`. Inside this repo, Video uses Audio by project
reference, so a change to both is one branch and one pull request.

- **Visual Studio:** open `FatzMedia.sln` (Audio's .NET binding and every Video project).
  `Audio/bindings/dotnet/FatzMedia.Audio.sln` and `Video/FatzMedia.Video.sln` still build each side alone.
- **Native audio library:** build it once with CMake before running the FatzAudio engine
  (`Audio/docs/building.md`; `Video/docs/NATIVE_DEPENDENCIES.md`, "Building the native library").
- **CI:** `.github/workflows`; the Audio workflows (`build`, `devices`, `dotnet-apps`) run on changes
  under `Audio/`, the `video-*` workflows on changes under `Video/` or `Audio/`.

Proprietary software: see [LICENSE](LICENSE).
