# Volume, pitch and tempo

Volume, pitch and tempo are live controls on the player's effects object. They apply to the open item at once, carry over to the next item, and raise change events your UI can bind to. This article covers the ranges, how to build a mute button, how pitch and tempo behave on different media, and how values are reset on load or on `Reset()`.

## The effects object

`player.Effects` is a <xref:FatzBomb.MediaControl.Controllers.MediaEffects>:

| Property | Type | Range | Default | Meaning |
|---|---|---|---|---|
| `Volume` | `double` | 0.0 to 1.0 | 1.0 | Output level. Linear: 0.5 is half amplitude, not half perceived loudness. |
| `Pitch` | `int` | -60 to +60 | 0 | Pitch shift in semitones, without changing speed. |
| `Tempo` | `int` | 5 to 5000 | 100 | Playback speed in percent, without changing pitch. 100 is normal; 50 is half speed. |

Values outside the range are clamped. `Normalize` and `Equalizer` are on the effects object too, and also directly on the player; see [Normalization](normalization.md) and [Equalizer](equalizer.md).

```csharp
player.Effects.Volume = 0.8;   // 80 %
player.Effects.Pitch = -2;     // two semitones down
player.Effects.Tempo = 90;     // 90 % speed

player.VolumeChanged += (_, e) =>
    dispatcher.Post(() => volumeLabel.Text = $"{e.CurrentValue:P0}");
player.PitchChanged += (_, e) =>
    dispatcher.Post(() => pitchLabel.Text = $"{e.CurrentValue:+0;-0;0}");
player.TempoChanged += (_, e) =>
    dispatcher.Post(() => tempoLabel.Text = $"{e.CurrentValue}%");
```

Setters take effect immediately and do not queue behind other control calls, so a slider can write them on every move. The three events are `EventHandler<MediaValueChangedEventArgs<T>>` with `PreviousValue`, `CurrentValue` and a `Reason`. They fire on background threads; marshal to the UI thread. Setting a value equal to the current one raises nothing.

> [!TIP]
> For a volume slider that feels natural, map the slider through a curve (for example `Volume = slider * slider`) before assigning it. The engine applies the value as a plain linear gain.

## Mute

There is no separate mute property. Set `Volume` to 0 and remember the previous value in your own code:

```csharp
public sealed class MuteButton(MediaControlPlayer player)
{
    private double _volumeBeforeMute = 1.0;

    public bool IsMuted { get; private set; }

    public void Toggle()
    {
        if (IsMuted)
            player.Effects.Volume = _volumeBeforeMute;
        else
        {
            _volumeBeforeMute = player.Effects.Volume;
            player.Effects.Volume = 0;
        }

        IsMuted = !IsMuted;
    }
}
```

## Behaviour by media type

- **Audio files:** all three controls work.
- **Video with sound:** volume, pitch and tempo apply to the soundtrack.
- **Video and CD+G without sound:** only tempo applies. It changes the speed of the playback clock, so the picture runs faster or slower. Volume and pitch are stored but have nothing to act on.
- **Images:** nothing to apply.
- **No media open:** the values are stored and applied when the next item opens. `Tempo` reads back the stored value.

Volume is independent of the other gain stages. Fades (see [Prepared next and transitions](../playback/prepared-next-and-transitions.md)), [normalization](normalization.md) and the silent seek-bar scrub all work in their own stage and multiply with it; none of them changes `Volume`.

## Persistence and reset

Volume, pitch and tempo stay as you set them when you load another item. Two switches change that:

- `ResetPitchOnMediaLoad` and `ResetTempoOnMediaLoad` (properties on the player, also in `MediaPlayerConfiguration`) return pitch to 0 and tempo to 100 after each successful load. A karaoke app usually wants both, so one singer's key change does not follow the next song. The events then carry `Reason == MediaChangeReason.LoadReset` and arrive before `MediaLoaded`.
- `Reset(configuration, paths)` rebuilds the engine and returns volume, pitch, tempo, normalization, equalizer and loop to their defaults. Each value that changed raises its event with `MediaChangeReason.PlayerReset`, after `Reset` returned.

`e.Reason` is `MediaChangeReason.User` for changes you made yourself, so a handler can ignore the echo of its own writes.

## Things to watch

- `Volume` is the player's volume, not the Windows system volume or the volume mixer entry of your process.
- A value set while the player is being rebuilt (during `Reset` or a mode switch) can be lost. Set it again once the rebuild finished. Mode switches themselves carry volume, pitch and tempo over.
