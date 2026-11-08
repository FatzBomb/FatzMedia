# Audio output devices

The player plays through BASS on one output device, chosen when the player is created. By default that is the system default device, and the player follows it when Windows changes it. This article covers listing the devices, selecting one, what happens when the default device is rerouted or a device disappears, and what your application can do about it.

## Listing devices

<xref:FatzBomb.MediaControl.AudioOutputDevices> (in the Windows package) lists the usable output devices. The first entry is always the synthetic "Primary (device default)" entry.

```csharp
foreach (AudioOutputDeviceInfo device in AudioOutputDevices.Enumerate())
    Console.WriteLine($"{device.DeviceIndex}: {device.DisplayName} (primary: {device.IsPrimary})");
```

Each <xref:FatzBomb.MediaControl.AudioOutputDeviceInfo> has a `DeviceIndex`, `Name`, `Driver`, `IsPrimary`, `IsEnabled` and `IsInitialized`. `DisplayName` is the text for a selector. Disabled devices are left out.

`Enumerate()` uses the native-library paths configured in `DLLPath`; `Enumerate(nativeLibraryPaths)` takes your own list, the same one you pass when creating a player. When the BASS libraries are not loaded the list is empty. On Linux, the BASS device list is available from `BassAudioOutputDevices.Enumerate()` in `FatzBomb.MediaControl.Core.Audio`.

## Selecting a device

The device is part of the player's configuration:

```csharp
var configuration = new MediaPlayerConfiguration
{
    AudioOutputDeviceIndex = device.DeviceIndex,
    AudioOutputFallbackMode = AudioOutputFallbackMode.NoFallback,
};
```

| Setting | Values | Default |
|---|---|---|
| `AudioOutputDeviceIndex` | `AudioOutputDeviceInfo.PrimaryDeviceIndex` (-1) for the system default, or a device index from `Enumerate()` | -1 |
| `AudioOutputFallbackMode` | `FallbackToPrimary`: use the default device if the chosen one cannot be opened. `NoFallback`: do not play on another device. | `FallbackToPrimary` |

The device is chosen once, when the player starts. To change it later, call `Reset` with a new configuration:

```csharp
bool ok = player.Reset(newConfiguration, nativeLibraryPaths);
```

`Reset` blocks the calling thread and rebuilds the engine. No media is loaded afterwards: your application must reload the item and resume playback. Volume, pitch, tempo and the other runtime settings go back to their defaults (each change raises its event with `MediaChangeReason.PlayerReset`), while presentation surfaces stay attached. Call it from a worker thread, not from the UI thread.

If the chosen device cannot be initialised and the mode is `FallbackToPrimary`, the engine raises a `Bootstrap` warning diagnostic and uses the default device. With `NoFallback`, an error diagnostic is raised, no audio output is available, and loads that need audio fail.

## Following the default device

With `AudioOutputDeviceIndex = -1` (Primary), BASS follows Windows. When the user changes the default playback device, or Windows reroutes audio, BASS moves the stream with it and playback continues. The player does not stop, pause or reopen anything; it leaves the stream to BASS and Windows.

The player does tell you about it. A monitor checks the output once a second and raises `AudioDeviceChanged`:

```csharp
player.AudioDeviceChanged += (_, e) =>
    dispatcher.Post(() => statusText.Text = e.Kind switch
    {
        AudioDeviceChangeKind.DefaultDeviceChanged => $"Output is now {e.DeviceName}",
        AudioDeviceChangeKind.DeviceRemoved => "Audio output lost, retrying...",
        AudioDeviceChangeKind.DeviceAdded => "Audio output is back",
        _ => e.FailureMessage ?? e.Kind.ToString(),
    });
```

<xref:FatzBomb.MediaControl.AudioDeviceChangedEventArgs> carries:

| Member | Meaning |
|---|---|
| `Kind` | `DefaultDeviceChanged`, `DeviceStateChanged`, `DeviceRemoved`, `DeviceAdded` or `Unrecoverable`. |
| `DeviceName` | Friendly name of the affected endpoint, when known. |
| `DeviceId` | Windows endpoint id, when known. |
| `FailureMessage` | Human-readable text. For a default-device change it describes the change; it is not an error. |
| `HResult` | Windows HRESULT, or 0 when there is none. |

What the kinds mean in practice:

- **`DefaultDeviceChanged`:** the Windows default output changed (on Windows the monitor compares the default endpoint each second). Playback follows when the player uses Primary. A player pinned to an explicit device keeps using that device.
- **`DeviceStateChanged`:** the output's sample rate changed.
- **`DeviceRemoved` / `DeviceAdded`:** the output became unavailable or came back. This also covers another application taking the device exclusively, or a USB device being unplugged. While the output is lost, the engine tries to restart it once a second and resumes the channels that were playing when it comes back. `FailureMessage` on the removal says so.

The core never changes the transport state because of these events; BASS and Windows keep responsibility for the stream. The same information is also logged as `MediaDiagnostic` warnings (categories `AudioDeviceMonitor` and `AudioOutput`).

The event fires on a background thread. Marshal to the UI thread before you touch controls.

> [!NOTE]
> The output watchdog is shared by all players in the process. When the output is lost, every player raises `DeviceRemoved`, then `DeviceAdded` when it returns.

## Things to watch

- A `Background`-mode player opens no audio device and raises no device events. See [Player modes](../playback/player-modes.md).
- Device indexes can change between runs and after a device is plugged in. Store the device name and look the index up again at startup instead of saving the number.
- `MediaErrorCode.AudioDeviceLost` exists in the error codes but the engine does not raise it today. Use `AudioDeviceChanged`.
- On Linux the monitor polls the BASS device state; the interval is `LinuxPlatformOptions.AudioDevicePollInterval` (default 1 second, 0.25 to 10).
- Bluetooth and USB outputs add latency that BASS does not report. `MediaPlayerConfiguration.EnableUsbBluetoothLatencyHeuristic` (off by default) adds a conservative per-device-class estimate to the output latency the engine uses for audio and video sync. If the picture still drifts, use the [sync offsets](sync-offsets.md).
