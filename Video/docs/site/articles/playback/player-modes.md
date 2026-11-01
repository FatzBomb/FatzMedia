# Player modes

A player runs in one of three modes: `Full`, `Background` or `Audio`. The mode decides what the player builds and what it accepts, so a mode that does less also costs less. Use `Background` for a looping picture behind your UI, `Audio` for a player with no video output at all, and `Full` for everything else. You can switch the mode of a running player.

The modes are the values of <xref:FatzBomb.MediaControl.Core.Configuration.MediaPlayerMode>.

## What each mode does

| | Full | Background | Audio |
|---|---|---|---|
| Audio output | Yes | No (no audio device is opened, no device monitor runs) | Yes |
| Presentation (renderer, GPU device, surfaces) | Yes | Yes | No |
| Looping | `LoopMedia` | Always on | `LoopMedia` |
| Needs BASS | For audio media | No | Yes |

What each mode accepts:

| Media | Full | Background | Audio |
|---|---|---|---|
| Audio files, MIDI | Plays | Refused | Plays |
| Video files | Picture and sound | Picture only (no audio is decoded) | Soundtrack only, no video decoder |
| Video file without an audio stream | Plays | Plays | Refused |
| Audio + CD+G (MP3+G) | Audio and graphics | Refused | Audio only, graphics ignored |
| CD+G graphics alone | Plays | Refused | Refused |
| Images and BGRA / solid-colour sources | Plays | Plays | Refused |
| Network audio | Plays | Refused | Plays |
| Network video | Plays | Picture only; loops where the source is seekable | Soundtrack only |
| Backgrounds and subtitles | Yes | Not applicable | None (no backgrounds are loaded) |

A refused load returns `false`, raises a `MediaDiagnostic` warning in the category `PlayerMode` ("Background mode does not accept ...; X was not loaded.") and leaves the player as it was. Use the table to decide which mode fits your scenario; for example a game that shows an animated menu background uses a `Background` player, and a separate `Full` or `Audio` player plays the music.

## Choosing a mode at creation

Set `Mode` in the configuration you pass when you create the player:

```csharp
MediaPlayerConfiguration backgroundConfig = new() { Mode = MediaPlayerMode.Background };
```

`player.Mode` reports the current mode and `player.HasPresentation` is `false` in `Audio` mode.

### Full

The complete player. It is the default.

### Background

A looping picture source. It plays video (without decoding audio), images, and sources such as `MediaInputSource.FromColor`. No audio output is initialised, so it works without BASS. Looping is always on: setting `LoopMedia = false` is ignored and a diagnostic is raised. Pair it with a Full or Audio player when you need sound.

```csharp
await backgroundPlayer.LoadAsync(MediaInputSource.FromColor(0xFF101820));
await backgroundPlayer.PlayAsync();
```

### Audio

Audio output only. The player creates no renderer, GPU device or video decoder, which makes it the lightest option for a music player or a service that only needs sound. Presentation surfaces you attach stay registered, but nothing is drawn to them (`HasPresentation` is `false`), so your adapter should show its own placeholder. Calls that attach surfaces return `false` while in this mode.

## Switching on the fly

`SetModeAsync` rebuilds the player for another mode and reopens what was playing:

```csharp
player.ModeChanged += (_, e) => Console.WriteLine($"{e.PreviousValue} -> {e.CurrentValue}");
player.HasPresentationChanged += (_, e) => Console.WriteLine($"Presentation: {e.CurrentValue}");

bool ok = await player.SetModeAsync(MediaPlayerMode.Audio);
```

What happens:

1. The new mode's engine is built and the old one released.
2. The open media is reopened as the same item, at the same position and in the same transport state (playing, paused or stopped). There is a short gap, like a seek. `MediaLoaded` is raised again for the reopened item.
3. `ModeChanged` is raised, and `HasPresentationChanged` too when presentation turned on or off (into or out of `Audio`).

Your settings carry over: loop, volume, pitch, tempo, normalization, equalizer, sync offsets, display options, `PlayOnLoad`, the reset-on-load options and the prepared-next item. Surfaces are kept while in `Audio` mode and attached again when you return to a mode with presentation.

The task returns `true` when the player is in the requested mode afterwards, including when it already was (nothing is rebuilt). Two cases to handle:

- Media the new mode does not accept is not reopened. The player ends closed with a `PlayerMode` warning, and the call still returns `true`. Check `player.IsOpen` or watch `MediaClosed` and diagnostics.
- If the new mode cannot be built, the player is rebuilt in its previous mode and the call returns `false`.

An undefined enum value throws `ArgumentOutOfRangeException`.

> [!NOTE]
> Switching a player that is playing video into `Audio` mode keeps the sound and drops the picture. Switching back reopens the video at the current position.

## Other mode-related options

- `AutoBackground` (default `true`) controls whether loads search for sidecar and archive backgrounds. In `Audio` mode it reads `false`.
- `TransparentBackground` (default `false`) leaves areas where nothing is drawn transparent instead of black, for hosts that composite the output over their own layers.

## Things to watch

- Events from a switch arrive on background threads, like every player event. Marshal to the UI thread.
- Mode changes rebuild the engine, so they are heavier than a seek. Do not switch modes repeatedly in a loop; keep one player per role when both modes are needed at once.
- Read `HasPresentation` again after marshalling a `HasPresentationChanged` event: a quick switch back may already have changed it.
