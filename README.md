# FatzMedia

Media engines by FatzBomb Entertainment. Two products in one repository, each built,
versioned and shipped as its own packages:

| Folder | Product | What it is | Packages |
|---|---|---|---|
| [`Audio/`](Audio/README.md) | FatzMedia.Audio (today FatzAudio) | Cross-platform audio engine: native C library with a .NET binding. Needs nothing else. | `FatzAudio` (becomes `FatzMedia.Audio`) |
| [`Video/`](Video/README.md) | FatzMedia.Video (today FatzBomb.MediaControl) | .NET media engine for playback with a picture: video, A/V timing, karaoke presentation, renderers and UI adapters. Plays audio through FatzMedia.Audio. | `FatzBomb.MediaControl.*` (become `FatzMedia.Video.*`) |

An audio-only app references only the Audio package. The rename of namespaces and package ids
to `FatzMedia.Audio` and `FatzMedia.Video` is a separate step; until then the code keeps its
current names.

- Building Audio: [`Audio/docs/building.md`](Audio/docs/building.md).
- Building Video: [`Video/README.md`](Video/README.md) and
  [`Video/docs/NATIVE_DEPENDENCIES.md`](Video/docs/NATIVE_DEPENDENCIES.md).
- CI: `.github/workflows`; the Audio workflows (`build`, `devices`, `dotnet-apps`) run on changes
  under `Audio/`, the `video-*` workflows on changes under `Video/`.

Proprietary software: see [LICENSE](LICENSE).
