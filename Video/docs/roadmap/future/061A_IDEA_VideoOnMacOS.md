# Stage 061A (planned) - Video on macOS

Owner 2026-11-13. Kamujo and TriviaBomb are Windows now and Mac later, so Video needs macOS.

- Today Video lists osx as not supported. Needed: an FFmpeg package for macOS (see 058A), a renderer
  (Vulkan through MoltenVK, or Metal: decide in the stage), Avalonia on Mac, and platform services
  (026B had macOS deferred).
- FatzAudio already builds a universal macOS library, so audio needs no work here.
- Apple silicon is first-class; fold the osx-arm64 build into this stage (059A covers Windows and Linux).
- Several weeks. Smoke needs a Mac, which this sandbox does not have.
