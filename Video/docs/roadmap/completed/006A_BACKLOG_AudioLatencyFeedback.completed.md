# Completed Stage — 006A Audio Output Latency Feedback

Status: Complete
Completed: 2026-07-07

## Original Goal

Feed measured/known audio output latency into video/CDG presentation timing.

## What Changed

- **`IOutputLatencyProvider` interface** (`Core/Abstractions/IOutputLatencyProvider.cs`): single `GetOutputLatencySeconds()` method.
- **`BassOutputLatencyProvider`** (`Core/Audio/BassOutputLatencyProvider.cs`): reads `MBass.GetInfo().Latency`, opt-in heuristic adds conservative per-class offsets (50ms USB, 150ms Bluetooth) via `MediaPlayerConfiguration.EnableUsbBluetoothLatencyHeuristic`.
- **`AudioEngine.ApplyOutputLatency()`** and **`VideoAudioEngine.ApplyOutputLatency()`**: subtracts BASS-reported latency from position getters, snapshot publishes, and seek-position publishes. Switches BASS `CurrentDevice` before querying so latency reads are device-accurate.
- **`AudioPresentationClock`**: unchanged — latency is subtracted at the engine snapshot output layer, not inside the clock.
- **Config**: `MediaPlayerConfiguration.EnableUsbBluetoothLatencyHeuristic` (default `false`).

## Files Changed

- `Core/Abstractions/IOutputLatencyProvider.cs` (new)
- `Core/Audio/BassOutputLatencyProvider.cs` (new)
- `Core/Audio/AudioEngine.cs` — latency in Position getter/setter and Open paths
- `Core/Audio/VideoAudioEngine.cs` — latency in Position, seek, snapshot, and CreateBassStreams
- `Core/Configuration/MediaPlayerConfiguration.cs` — `EnableUsbBluetoothLatencyHeuristic`
- `MediaPlayerServiceProvider.cs` — wires latency provider into DI
- `Tests/BassOutputLatencyProviderTests.cs` (new, 11 tests)
