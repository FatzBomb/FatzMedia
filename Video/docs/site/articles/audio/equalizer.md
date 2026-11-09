# Equalizer

The player has a built-in parametric equalizer. You describe it with an <xref:FatzMedia.Video.AudioEqualizerSettings> value (on or off, plus up to 16 bands) and assign it to `player.Equalizer`. It applies live to the playing audio (local files and network streams), to the soundtrack of video files, and to every item loaded afterwards. A ready-made 10-band graphic equalizer is one method call.

The equalizer is off by default. While it is off, or while every band is at 0 dB, no audio filter is attached and the output is bit-identical to having no equalizer.

## A 10-band graphic equalizer

`AudioEqualizerSettings.CreateGraphic10Band` takes the gains in dB for the ten ISO octave centres (31.5, 63, 125, 250, 500 Hz, 1, 2, 4, 8 and 16 kHz). Missing values are 0 dB. The result is already enabled.

```csharp
// Bass and treble lift.
player.Equalizer = AudioEqualizerSettings.CreateGraphic10Band(5, 4, 2, 0, 0, 0, 0, 1, 2, 3);

// Switch it off again.
player.Equalizer = AudioEqualizerSettings.Disabled;
```

The center frequencies are available as `AudioEqualizerSettings.Graphic10BandCentersHz`, which is handy for labelling sliders. The test players bind ten sliders to these bands and call `CreateGraphic10Band` whenever one moves.

## Custom bands

For a parametric setup, create <xref:FatzMedia.Video.AudioEqualizerBand> values yourself:

```csharp
player.Equalizer = new AudioEqualizerSettings
{
    Enabled = true,
    Bands =
    [
        new AudioEqualizerBand(centerHz: 100, gainDb: 4),
        new AudioEqualizerBand(centerHz: 3000, gainDb: -3, bandwidthOctaves: 2),
    ],
};
```

Each band is a peaking filter:

| Parameter | Range | Default |
|---|---|---|
| `centerHz` | 20 to 20000 | none |
| `gainDb` | -15 to +15 (boost or cut) | none |
| `bandwidthOctaves` | 0.1 to 4 | 1 |

Values outside the range are clamped; a non-finite value falls back to a neutral default. A settings object holds at most `AudioEqualizerSettings.MaximumBands` (16) bands; extra ones are dropped.

## Switching on and off

`AudioEqualizerSettings` is an immutable record, so toggle it with `with`. Bands are kept, which lets a UI offer a bypass button without losing the user's curve:

```csharp
player.Equalizer = player.Equalizer with { Enabled = false };   // bypass
player.Equalizer = player.Equalizer with { Enabled = true };    // back on
```

`IsEffective` is `true` when the equalizer is enabled and at least one band is not at 0 dB, that is, when a filter is actually in the signal path.

## Presets

The library has no preset list. Presets are plain `AudioEqualizerSettings` values, so keep them in your application, for example as a dictionary from a name to an array of ten gains, and apply the one the user picks. Settings compare by value (`Enabled` and the bands), so you can compare the current setting with a preset to highlight the matching entry.

## Change notification

```csharp
player.EqualizerChanged += (_, e) =>
    dispatcher.Post(() => Refresh(e.CurrentValue));
```

`EqualizerChanged` fires on the thread that set the value (after `Reset`, on an engine thread) when the value changes; marshal to the UI thread. It does not fire when you assign a value equal to the current one. After `Reset()` the equalizer goes back to disabled and the event is raised with `MediaChangeReason.PlayerReset`.

## Things to watch

- The equalizer is stored on the player, so it stays when you load another item or switch the player mode.
- It applies to audio the player outputs. A video without a sound stream, CD+G graphics and images have nothing to filter.
- The equalizer needs the BASS libraries, like all audio output. See [Supported formats](../playback/supported-formats.md).
