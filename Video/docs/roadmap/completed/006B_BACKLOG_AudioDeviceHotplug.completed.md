# Completed Stage — 006B/C Audio Device Hotplug + Device Selection

Status: Complete
Completed: 2026-07-07

## Original Goal (006B)

Handle audio device/default-device changes without silent audio failure.

## Extended Goal (006C)

Creation-time audio output device selection and fallback policy for multi-device
scenarios (DJ/karaoke: main output vs. headphone preview).

## What Changed

### 006B — Audio Device Hotplug Monitoring

- **`IAudioDeviceMonitor` interface** (`Core/Abstractions/IAudioDeviceMonitor.cs`): internal poll loop contract.
- **`AudioDeviceChangeInfo` / `AudioDeviceChangeKind`** records: internal change kind (default changed, state changed, removed, added, unrecoverable).
- **`PollingAudioDeviceMonitor`** (`Core/Audio/PollingAudioDeviceMonitor.cs`): 1-second poll of BASS state + Windows default endpoint via `CoreAudioDefaultEndpointReader`. Detects device-index changes, sample-rate changes, default-endpoint ID/name changes.
- **`CoreAudioDefaultEndpointReader`** (`Core/Audio/CoreAudioDefaultEndpointReader.cs`): COM interop (IMMDeviceEnumerator) to read the Windows default render endpoint ID and friendly name.
- **`AudioDeviceChanged` event on `IMediaControlSession`**: public event with `AudioDeviceChangedEventArgs` wrapping internal change info.
- **`BASS_CONFIG_DEV_NONSTOP`**: enabled at BASS init via `AudioEngine.EnsureDevNonStop()` to keep device buffer alive during transient loss.
- **Diagnostic-only policy**: after field testing proved all Core interventions (stop, reload, re-seek, BASS reinit) made behavior worse, device-change notifications are purely diagnostic. BASS/Windows handle active-stream migration. No `Stop`, `Close`, `Load`, `Seek`, or `Play` is triggered from monitor events.

### 006C — Audio Output Device Selection + Fallback

- **`AudioOutputDeviceInfo` record** (`Api/AudioOutputDeviceInfo.cs`): public device descriptor with `DeviceIndex`, `Name`, `Driver`, `IsPrimary`, `IsEnabled`, `IsInitialized`, synthetic `Primary` singleton, and `DisplayName`.
- **`AudioOutputDevices.Enumerate()`** (`Api/AudioOutputDevices.cs`): public static enumerator that loads native DLLs and queries BASS `GetDeviceInfo`. Filters out the "Default" list entry and disabled devices.
- **`BassAudioOutputDevices`** (`Core/Audio/BassAudioOutputDevices.cs`): internal helpers — enumerate BASS outputs, `TrySetCurrentDevice`, `ResolveInitializedDeviceIndex`.
- **`MediaPlayerConfiguration.AudioOutputDeviceIndex`**: creation-time BASS device index (`-1` = Primary). Not a live-switching property.
- **`MediaPlayerConfiguration.AudioOutputFallbackMode`**: `FallbackToPrimary` (default) or `NoFallback`. Applied at player creation/open time.
- **`IAudioOutputDeviceProvider`** (`Core/Abstractions/IAudioOutputDeviceProvider.cs`): internal interface on `AudioEngine` exposing the resolved `BassDeviceIndex` for the video pipeline to use.
- **Both audio engines** resolve and select the configured BASS device via `BassAudioOutputDevices.TrySetCurrentDevice` before stream creation and latency queries.
- **Fallback logic in `MediaPlayerServiceProvider`**: if explicit device init fails and `FallbackToPrimary`, retries on `-1` with a diagnostic.

### Session Reset

- **`IMediaControlSession.Reset(MediaPlayerConfiguration, IEnumerable<string>)`**: stops all playback, disposes the internal DI container and all engines/coordinators, rebuilds with the new config, preserves and reattaches existing presentation surfaces. Lets hosts change devices or fallback policy without creating a new control instance.

## Files Changed

**New files:**
- `Api/AudioDeviceChangedEventArgs.cs`
- `Api/AudioOutputDeviceInfo.cs`
- `Api/AudioOutputDevices.cs`
- `Core/Abstractions/IAudioDeviceMonitor.cs`
- `Core/Abstractions/IAudioOutputDeviceProvider.cs`
- `Core/Audio/BassAudioOutputDevices.cs`
- `Core/Audio/CoreAudioDefaultEndpointReader.cs`
- `Core/Audio/PollingAudioDeviceMonitor.cs`
- `Tests/PollingAudioDeviceMonitorTests.cs` (11 tests)
- `Tests/AudioOutputConfigurationTests.cs` (2 tests)

**Modified:**
- `Api/IMediaControlSession.cs` — `AudioDeviceChanged` event, `Reset` method
- `Api/MediaControlPlayer.cs` — monitor wiring, event dispatch, `Reset` implementation
- `Api/MediaErrorCode.cs` — `AudioDeviceLost` value, updated xmldoc
- `Core/Audio/AudioEngine.cs` — `Init(device)` honors configured device, `EnsureDevNonStop`, `IAudioOutputDeviceProvider`
- `Core/Audio/VideoAudioEngine.cs` — `_bassDeviceIndex` propagation
- `Core/Configuration/MediaPlayerConfiguration.cs` — `AudioOutputDeviceIndex`, `AudioOutputFallbackMode`
- `MediaPlayerServiceProvider.cs` — device init with fallback
- `Pipeline/PlaybackCoordinator.cs` — `bassDeviceIndex` → `VideoPipeline`
- `Pipeline/VideoPipeline.cs` — `_bassDeviceIndex` → `VideoAudioEngine`
- `Pipeline/PipelineMediaEngine.cs` — no-op (line-ending only)
