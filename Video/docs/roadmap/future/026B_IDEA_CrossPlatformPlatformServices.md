# Idea Stage 026B - Cross-Platform Platform Services

Status: Good idea.

## Why This Is Worth Doing

True cross-platform playback needs OS packages that supply native-library loading,
audio-device integration, precision waiting, hardware-decode policy, and other services
behind Core's existing platform contracts. Renderers and UI adapters should not absorb
those responsibilities.

## Recommended Package Direction

- Use `FatzBomb.MediaControl.Windows` as the reference implementation, not as a shared
  platform base class or dependency.
- `FatzBomb.MediaControl.Linux` for Linux native services and decode policy.
- `FatzBomb.MediaControl.MacOS` for macOS native services and decode policy.
- Optional thin integration bundles can pair one platform with one renderer, matching
  the existing Windows plus Direct3D11 package pattern.

## Recommended Implementation Order

1. Create a platform-service capability matrix from the current Windows implementation.
2. Implement a Linux software-decode baseline with host-owned native libraries.
3. Add Linux audio-device integration and renderer pairing.
4. Implement the macOS baseline using the same Core contracts.
5. Add hardware-decode integrations as independent follow-up slices.

## Recommended Immediate Action

Map every `IMediaControlPlatformServices` registration and classify it as portable,
Windows-specific, Linux, or macOS. Define the smallest Linux package that can initialize
host-supplied BASS/FFmpeg, provide precision waits, and play through software decode.

## Must Preserve

- Core remains OS- and architecture-neutral.
- Hosts retain responsibility for native binary source, licensing, version, and deployment.
- Platform and renderer selection remain independent.
- Existing Windows behavior and native package layout remain unchanged.

## Explicit Non-Goals

- Bundling or redistributing native media binaries without an explicit policy decision.
- Moving renderer, UI, or playback policy into platform packages.
- Requiring hardware decode in the first Linux or macOS slice.
- Creating one platform package filled with runtime OS branches.
