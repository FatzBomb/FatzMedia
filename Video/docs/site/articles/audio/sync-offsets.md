# Sync offsets

Sync offsets let the user correct timing that is slightly off: video that lags the sound, CD+G graphics that arrive late, or subtitles that show too early. The player keeps three independent offsets, one each for the video picture, the CD+G graphics and the subtitles. Sound is the reference clock and is never moved. This article covers the properties, the direction of each offset, the step helpers and the limits.

## The three offsets

| Property | Shifts | Affects |
|---|---|---|
| `AudioSyncOffset` | The video picture, relative to the audio clock | Video files |
| `CdgSyncOffset` | The CD+G graphics timeline | CD+G karaoke (audio + `.cdg`) |
| `SubtitleSyncOffset` | The subtitle timeline | SRT/ASS subtitles, embedded subtitles, MIDI/KAR lyrics |

All three are `TimeSpan` properties with a getter and a setter.

> [!NOTE]
> `AudioSyncOffset` is named for the usual complaint (lip sync) but it moves the picture, not the audio. It has no effect on audio-only items.

### Direction

A positive offset shows content earlier than the clock says; a negative offset shows it later.

- `AudioSyncOffset = +100 ms`: the picture shows the frame that belongs 100 ms later in the file. Use a positive value when the picture lags behind the sound.
- `CdgSyncOffset = +100 ms`: the graphics are rendered as of 100 ms later. Use a positive value when the graphics lag.
- `SubtitleSyncOffset = +2 s`: subtitle cues appear 2 s earlier. Use a positive value when subtitles come too late, negative when they come too early.

```csharp
player.AudioSyncOffset = TimeSpan.FromMilliseconds(120);
player.SubtitleSyncOffset = TimeSpan.FromSeconds(-2.5);
```

## Stepping

For keyboard shortcuts or +/- buttons, use the step helpers. Each step is 100 ms; they return the resulting offset.

```csharp
TimeSpan cdg = player.AdjustCdgSyncOffset(-1);      // 100 ms less
TimeSpan video = player.AdjustAudioSyncOffset(+3);  // 300 ms more
TimeSpan subs = player.AdjustSubtitleSyncOffset(+1);
```

`ResetSyncOffsets()` sets all three back to zero.

## Limits and lifetime

- Each offset is limited to -30 s to +30 s. Larger values are clamped, and the setter keeps the clamped value (read the property back to see it). A non-finite value becomes zero.
- Offsets belong to the loaded item. They return to zero when the item is closed or another one is loaded, so a correction for one karaoke file does not leak into the next. Remember the value in your own settings if you want to store it per file, and write it back after `MediaLoaded`.
- A mode switch (`SetModeAsync`) keeps the offsets of the item it reopens.
- The video picture cannot show a position before the start or after the end of the file. A positive video offset near the end holds the last frame, and a negative one near the start holds the first.
- Changes apply at once. With playback paused, the picture, graphics or subtitle refreshes to the new timing.

> [!TIP]
> Show the offset in the UI in milliseconds and offer the step buttons plus a reset. Tuning by ear with 100 ms steps is the usual way to fix a Bluetooth headset or a TV that adds delay.

## Things to watch

- The properties and helpers act on the current engine and do not wait in the control queue. Do not call them while a `Reset` or mode switch is running; the value may be lost. Set the offset again once the rebuild is done.
- There is no offset for the audio itself. If the sound is late on a particular output device, shift the picture instead (positive `AudioSyncOffset` when the picture lags). For a device with fixed extra delay, `MediaPlayerConfiguration.EnableUsbBluetoothLatencyHeuristic` can compensate automatically for USB and Bluetooth outputs. It is an estimate; see [Audio output devices](output-devices.md).
