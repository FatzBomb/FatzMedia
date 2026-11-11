# Audio output devices

The player plays on one output device, chosen when the player is created. By default that is the system default device, and the player follows it when the system changes it. This article covers listing the devices, selecting one, what happens when the default device is rerouted or a device disappears, and what your application can do about it.

## Listing devices

<xref:FatzMedia.Video.Audio.FatzAudioOutputDevices> lists the usable output devices. The first entry is always the synthetic "Primary (device default)" entry. It needs the FatzAudio native library.

```csharp
foreach (AudioOutputDeviceInfo device in FatzAudioOutputDevices.Enumerate())
    Console.WriteLine($"{device.Id ?? "(default)"}: {device.DisplayName} (primary: {device.IsPrimary})");
```

Each <xref:FatzMedia.Video.AudioOutputDeviceInfo> has an `Id` (`null` for the default entry), a `Name` and `IsPrimary`. `DisplayName` is the text for a selector.

## Selecting a device

The device is part of the player's configuration:

```csharp
var configuration = new MediaPlayerConfiguration
{
    AudioOutputDeviceId = device.Id,
    AudioOutputFallbackMode = AudioOutputFallbackMode.NoFallback,
};
```

| Setting | Values | Default |
|---|---|---|
| `AudioOutputDeviceId` | `null` for the system default, or a device id (or the exact device name, case ignored) from `Enumerate()` | `null` |
| `AudioOutputFallbackMode` | `FallbackToPrimary`: use the default device if the chosen one cannot be opened. `NoFallback`: do not play on another device. | `FallbackToPrimary` |

The device is chosen once, when the player starts. To change it later, call `Reset` with a new configuration:

```csharp
bool ok = player.Reset(newConfiguration, nativeLibraryPaths);
```

`Reset` blocks the calling thread and rebuilds the engine. No media is loaded afterwards: your application must reload the item and resume playback. Volume, pitch, tempo and the other runtime settings go back to their defaults (each change raises its event with `MediaChangeReason.PlayerReset`), while presentation surfaces stay attached. Call it from a worker thread, not from the UI thread.

If the chosen device cannot be opened and the mode is `FallbackToPrimary`, the engine raises a `Bootstrap` warning diagnostic and uses the default device. With `NoFallback`, an error diagnostic is raised, no audio output is available, and loads that need audio fail. A chosen device that goes away while playing moves to the default.

## Following the default device

With `AudioOutputDeviceId = null` (the default), the player follows the system. When the user changes the default playback device, FatzAudio moves the output with it and playback continues. The player does not stop, pause or reopen anything.

The player does tell you about it. FatzAudio's device watch raises `AudioDeviceChanged`:

```csharp
player.AudioDeviceChanged += (_, e) =>
    dispatcher.Post(() => statusText.Text = e.Kind switch
    {
        AudioDeviceChangeKind.DefaultDeviceChanged => $"Output is now {e.DeviceName}",
        AudioDeviceChangeKind.DeviceRemoved => "Audio output lost",
        AudioDeviceChangeKind.DeviceAdded => "A new audio device appeared",
        _ => e.FailureMessage ?? e.Kind.ToString(),
    });
```

<xref:FatzMedia.Video.AudioDeviceChangedEventArgs> carries:

| Member | Meaning |
|---|---|
| `Kind` | `DefaultDeviceChanged`, `DeviceStateChanged`, `DeviceRemoved`, `DeviceAdded` or `Unrecoverable`. |
| `DeviceName` | Friendly name of the affected endpoint, when known. |
| `DeviceId` | Device id, when known. |
| `FailureMessage` | Human-readable text. For a default-device change it describes the change; it is not an error. |
| `HResult` | Platform error code, or 0 when there is none. |

What the kinds mean in practice:

- **`DefaultDeviceChanged`:** the system default output changed. Playback follows when the player uses the default. A player pinned to an explicit device keeps using that device.
- **`DeviceAdded`:** a playback device appeared.
- **`DeviceRemoved`:** the device the player plays on went away. FatzAudio moves the output to the default device. A different device going away is a `DeviceStateChanged`.
- **`DeviceStateChanged`:** another change to the device list or to a device.

The core never changes the transport state because of these events; the audio engine moves the output itself. The same information is also logged as `MediaDiagnostic` warnings (categories `AudioDeviceMonitor` and `AudioOutput`).

The event fires on a background thread. Marshal to the UI thread before you touch controls.

## Things to watch

- A `Background`-mode player opens no audio device and raises no device events. See [Player modes](../playback/player-modes.md).
- Device ids can change between runs and after a device is plugged in. Store the device name and match it again at startup; `AudioOutputDeviceId` accepts a name.
- `MediaErrorCode.AudioDeviceLost` exists in the error codes but the engine does not raise it today. Use `AudioDeviceChanged`.
- The same device events and device list work on Windows and Linux. On Linux, FatzAudio's null output device (used by the native tests) needs no sound server.
- If the picture drifts against the sound on a Bluetooth or USB output, use the [sync offsets](sync-offsets.md).
