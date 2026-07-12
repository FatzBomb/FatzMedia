# Current Architecture

## Projects

- `FatzBombMedia.MediaControl`: UI-agnostic Core media-control engine.
- `FatzBombMedia.MediaControl.Wpf`: strict WPF adapter for control/HWND lifecycle interop.
- `FatzBombMedia.Player`: WPF validation host.

Future adapters should be separate projects using the same package pattern:

- `FatzBombMedia.MediaControl.WinUI`
- `FatzBombMedia.MediaControl.Avalonia`
- `FatzBombMedia.MediaControl.WinForms`

## Ownership

Core owns playback, commands, media state, sidecar detection, background image/video lifecycle, Cdg, wash, retained layers, D3D11 composition, swap-chain creation, target registration, multi-target fan-out, and final presentation.

Adapters own only framework interop: control lifecycle, native surface creation, DPI/size, visibility, focus/input bridge, and framework-specific source conversion helpers.

Adapters must not own renderer selection, layer roles, frame processing, Cdg invalidation, background/video presentation, compositor timing, swap-chain policy, or final present logic.

## Runtime Flow

1. Host places a framework control, such as WPF `MediaSurface`.
2. Adapter creates or discovers a framework/native presentation surface.
3. Adapter passes a neutral `MediaPresentationSurfaceDescriptor` to Core.
4. Core attaches/resizes/detaches that surface.
5. Core builds the final composed frame from Layer 0 background/base, Layer 1 wash, and Layer 2 Cdg overlay.
6. Core presents the composed frame to all active targets.

## Video/Audio Demux Ownership

- Main video decoding owns the Core-managed video demux cursor used for video seek, buffering, and proactive loop pre-seek.
- `VideoAudioEngine` opens an independent FFmpeg cursor for the same source. This preserves codec priming/preroll semantics; routing initial audio packets through the video cursor caused a persistent startup offset with AV1 + E-AC-3 until the first seek.
- Coordinated seek still repositions both cursors to the same target before video warm-up and audio resume.
- Audio-backed gapless looping remains independent: video pre-seeks its cursor while `VideoAudioEngine` wraps its own decoder without stopping the BASS channel.

## Device-Lost Recovery

Core detects and recovers D3D11 device loss (Stages 005A/005B/005C) entirely inside Core; adapters are never involved.

- `D3D11DeviceLostClassifier` classifies device-removed/reset conditions on both sides: HRESULT-based for the Core-owned presentation device, and an `IsDeviceLost(IntPtr)` overload for the FFmpeg-owned hardware-decode device (a raw native pointer, wrapped best-effort and never false-positive).
- Two independent recovery orchestrators, matching the two independently owned devices: the presentation side in `NativeMediaPresentationCoordinator` (rebuilds device, swap chains, DirectComposition target, and secondary outputs), and the decode side in `VideoPipeline` (runtime software-decode fallback with background hardware re-probe/restore; audio and CDG keep running).
- Diagnostic categories follow a fixed naming convention: `"D3D11.DeviceLost"` (presentation) and `"Decoder.DeviceLost"` (decode), raised at Error on loss and Info on successful recovery, with a `.RecoveryFailed` suffix for the terminal diagnostic after bounded retries are exhausted.
- Both recovery paths reuse existing rebuild machinery (the proven surface-recreate and seek stop-reposition-restart sequences) — no new swap-chain, compositor, or renderer abstraction was introduced.

## Audio Output Latency Feedback (Stage 006A)

- `IOutputLatencyProvider` interface: `GetOutputLatencySeconds()` returns the BASS-reported output latency for the current device.
- `BassOutputLatencyProvider`: reads `MBass.GetInfo().Latency`; opt-in heuristic adds conservative per-class offsets for USB DAC and Bluetooth endpoints via `MediaPlayerConfiguration.EnableUsbBluetoothLatencyHeuristic`.
- Both `AudioEngine` and `VideoAudioEngine` apply output latency at position-read time: `Position` getter, snapshot publish, and seek-position publish all subtract the latency so `AudioPresentationClock` and hosts see corrected time.

## Audio Device Hotplug Monitoring (Stage 006B)

- `IAudioDeviceMonitor` / `PollingAudioDeviceMonitor`: 1-second poll of BASS state and Windows default endpoint via `CoreAudioDefaultEndpointReader`.
- `AudioDeviceChanged` event on `IMediaControlSession` with `AudioDeviceChangedEventArgs`.
- `BASS_CONFIG_DEV_NONSTOP` enabled at BASS init to keep the device buffer alive during transient loss.
- Policy is **diagnostic-only**: no `Stop`, `Close`, `Load`, `Seek`, or `Play` is triggered by device-change notifications. BASS/Windows handle active-stream migration; Core only raises diagnostics and the public event.

## Audio Output Device Selection (Stage 006C)

- `AudioOutputDeviceInfo` (public record) and `AudioOutputDevices.Enumerate(...)` enumerate BASS output devices through MediaControl.
- `MediaPlayerConfiguration.AudioOutputDeviceIndex`: BASS device index, `-1` = Primary (system default). Creation-time choice; runtime device changes require a new player or `Reset`.
- `MediaPlayerConfiguration.AudioOutputFallbackMode`: `FallbackToPrimary` (default) or `NoFallback`.
- If an explicit device is unavailable at init and fallback is enabled, the engine retries on Primary.
- Both `AudioEngine` and `VideoAudioEngine` resolve and select the configured BASS device before stream creation and latency queries.

## Session Reset

- `IMediaControlSession.Reset(MediaPlayerConfiguration, IEnumerable<string>)` stops all playback, disposes the internal DI container and all engines/coordinators, rebuilds with the new config, and reattaches any existing presentation surfaces.
- Hosts call this instead of creating a new control instance when changing devices or fallback policy.

## WPF Adapter API

`FatzBombMedia.MediaControl.Wpf.Player` derives from Core `MediaControlPlayer`.

This makes Core commands, properties, and events automatically available through the WPF adapter while keeping adapter-specific additions limited to WPF conversions such as `ImageSource` to `MediaInputSource`.

`MediaSurface` accepts an `IMediaControlSession`/player and supplies HWND surfaces to Core. Its `OverlayContent` is WPF UI chrome only; it is not a media overlay. Media overlay, wash, background, and final composition stay in Core.
