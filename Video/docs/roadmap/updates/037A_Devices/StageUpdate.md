# Stage Update

Stage: 037A - Output device choice and device events on the FatzAudio engine (BASS retirement items 2 and 3)
Date: 2026-11-10

## Status: implemented, pending manual smoke

The two missing features from `updates/037A_6/Readiness.md`: a host can pick the FatzAudio output device,
and a FatzAudio player reports devices added, removed and default changes without BASS.

## What changed

| Where | What |
|---|---|
| `MediaPlayerConfiguration.AudioOutputDeviceId` (public, new) | The FatzAudio device: an id from the device list, or its exact name (case ignored). Null (default): the system default, followed. Not found: the default with a warning that lists the devices, or an error with `AudioOutputFallbackMode.NoFallback`. `AudioOutputDeviceIndex` stays BASS's (a warning when it is set without a device id). |
| `AudioOutputDeviceInfo.Id` (public, new) | The FatzAudio device id; null for the default entry and for BASS devices. |
| `FatzAudioOutputDevices.Enumerate()` (public, `FatzMedia.Video.Audio`) | The default entry, then every playback device FatzAudio sees, with ids. Needs FatzAudio's library, not BASS. |
| `FatzAudioEngine` | Opens the chosen device (FatzAudio moves an explicit device that goes away to the default). Its `DeviceChanged` now watches FatzAudio's device list once a second while anyone listens and reports each device removed, each device added, then a new default. FatzAudio's per-stream device events only log. |
| `IAudioDeviceChangeSource.ReportsDeviceList` (internal) | True for the FatzAudio engine: the player's monitor then uses the engine alone and not the platform's BASS monitor, so each change is reported once (fixes the double report noted in slice 2). |
| Test players | `--audio-device=<id or name>` with `--audio=fatz` (WPF, WinForms, Avalonia). |

Why polling: FatzAudio's C core (miniaudio) has no device-list notification, and BASS's monitor polled once a
second too. A native notification (IMMNotificationClient, PulseAudio subscribe, Core Audio listeners) can
replace the poll later without changing the event contract; it is not needed for the retirement.

## Tests

`FatzAudioDevicesTests` (5): find by id then name; the device list starts with the default and carries ids;
the list diff reports removed, added and a new default in that order (also the default unplugged); on this
Windows PC the engine opened a real device by id and by name, fell back to the default for an unknown name,
and threw with `NoFallback`; the watch runs only while someone listens and a quiet list reports nothing.
`AudioEngineSelectionTests`: `--audio-device=` parsing.

## Manual smoke (owner)

1. `--audio=fatz --audio-device="<your USB or HDMI device name>"`: the Log says `ready on the <name> output
   device`; sound comes out of it. A wrong name: a warning listing your devices, sound on the default.
2. While playing on the default, plug in and unplug a USB headset, and change the default in Windows: one Log
   line each (`Audio device change: DeviceAdded ...`, `DeviceRemoved`, `DefaultDeviceChanged`), not two.
3. While playing on an explicit USB device, unplug it: playback moves to the default and goes on.
