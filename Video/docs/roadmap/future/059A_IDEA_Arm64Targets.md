# Stage 059A (planned) - ARM64 targets (win-arm64, linux-arm64)

Owner 2026-11-06. The managed code (Core, renderers, adapters) already runs on ARM64; Direct3D11 works on
ARM64 Windows, and Vulkan or the Software renderer serve ARM64 Linux.

- **FatzAudio:** builds win-arm64 and linux-arm64 (with osx universal, android and an iOS xcframework)
  in its CI and package, after rc.4 (rc.4 does not wait). Layout agreed:
  `runtimes/win-arm64/native/fatzaudio.dll`, `runtimes/linux-arm64/native/libfatzaudio.so`,
  `runtimes/osx/native/libfatzaudio.dylib` (universal), `runtimes/android-arm64/native/`.
- **Ours:** the Windows projects build for ARM64 as well as x64 (today `x64` only, `AGENTS.md`); native
  loading and the dependency report know the arm64 RIDs; ARM64 FFmpeg through 058A's packages; CI builds
  both architectures.
- **Smoke:** on an ARM64 Windows laptop (WPF, Avalonia, WinForms; Direct3D11 and hardware decoding)
  and an ARM64 Linux machine (Vulkan or Software). Not possible in this sandbox.
- After the FatzAudio pin with the arm64 libraries, and 058A's first packages.
- macOS arm64 is part of 061A (Video on macOS), not this stage.
