# CD+G

CD+G files hold the graphics of a karaoke song: the words, the colour changes and the
backgrounds drawn by the disc format. They are stored next to an audio file, or inside an archive
with it. The player pairs the two, plays them in sync and draws the graphics with the standard
presentation. This article covers how pairs are found, how to adjust the look and the timing, and
how seeking behaves.

## Loading a pair

Load any file of the pair. The player finds its partner.

```csharp
// song.mp3 and song.cdg are in the same folder.
bool loaded = await player.LoadAsync(@"C:\Karaoke\Artist - Song.mp3");

// Loading the .cdg works the same way.
loaded = await player.LoadAsync(@"C:\Karaoke\Artist - Song.cdg");

if (loaded && player.HasCdg)
{
    // The graphics are loaded. player.Content contains MediaContent.Cdg.
}
```

The rules for loose files:

- The audio and the `.cdg` must have the same name apart from the extension (`Song.mp3`,
  `Song.cdg`). Any audio format the player can play is fine: MP3, WAV, FLAC, OGG, M4A and so on.
- If you load the audio file, the player looks for a `.cdg` with the same name. If you load the
  `.cdg`, the player tries the known audio extensions until it finds an existing file.
- A `.cdg` with no audio next to it plays as graphics only, without sound.
- Probing uses lower-case extensions. On a case-sensitive file system (Linux), `Song.CDG` next to
  `Song.mp3` is not found; name the file `Song.cdg`.

`player.Content` tells you what was found. `MediaContent.Audio | MediaContent.Cdg` is a normal pair;
`MediaContent.Cdg` alone means graphics without audio.

## CD+G in archives

Archives are loaded like any other file. The extensions `.zip`, `.7z`, `.rar`, `.tar`, `.gz`,
`.bz2`, `.xz`, `.tgz` and `.tbz2` are recognised as archives.

```csharp
await player.LoadAsync(@"C:\Karaoke\Artist - Song.zip");
```

The player looks through the archive's entries and decides what to play in this order:

1. **A pair.** An audio entry and a `.cdg` entry with the same file name (ignoring folders,
   extension and case). The first `.cdg` that has a matching audio entry wins.
2. **Audio only.** If there is no matching pair but there is an audio entry, only the audio plays.
3. **CD+G only.** If there is no audio at all but there is a `.cdg` entry, the graphics play alone.
4. **Video.** If there is only a video entry, it plays as video.

If none of these exists, the archive is rejected and `LoadAsync` returns `false`.

> [!WARNING]
> If the audio and the `.cdg` inside the archive have different names (for example `track01.mp3`
> and `lyrics.cdg`), they are not paired. The player falls back to rule 2 and plays the audio
> without the graphics. Rename the entries so the names match.

An archive can also hold pictures or videos. The player uses those as the background behind the
graphics. See [Backgrounds](backgrounds.md).

## Changing how the graphics look

<xref:FatzMedia.Video.PlayerCore> has a `CdgVisuals` property of type
<xref:FatzMedia.Video.Controllers.CdgVisuals>. It has one setting.

| Property | Values | Default | Effect |
| --- | --- | --- | --- |
| `BackgroundOpacity` | 0 to 100 (higher values are limited to 100) | 100 | How opaque the CD+G background colour is. At 100 the background colour covers the picture behind it. Lower values let a background picture or video show through. At 0 only the words and graphics remain. |

```csharp
player.CdgVisuals.BackgroundOpacity = 40;   // a background video shows through
```

The setting is kept when you load the next song and when the player switches modes.

`PlayerCore.TransparentBackground` (default `false`) is a wider switch. When it is on,
nothing is drawn where there is no content: no background, no letterbox bars, and the CD+G
background colour is fully transparent, whatever `BackgroundOpacity` says. Hosts that composite
(WPF composited mode, Avalonia and frame sinks) then show whatever is underneath the video
surface. Turn it off again and `BackgroundOpacity` applies again.

> [!NOTE]
> The setting is on <xref:FatzMedia.Video.PlayerCore>, not on
> <xref:FatzMedia.Video.IPlayerSession>. The player inside a `KaraokeSurface` is a
> `PlayerCore`, so `karaokeSurface.Session as PlayerCore` reaches it.

## Scaling and effects

By default the player draws the classic 300x216 CD+G picture with sharp pixels and the GPU scales it to the
window. <xref:FatzMedia.Video.CdgScalingOptions> asks FatzAudio to draw a bigger, cleaner picture instead, optionally
with an outline, a shadow and a bevel. It is off until you set it, and a host sets it either at creation
(`MediaPlayerConfiguration.CdgScaling`) or at any time with `PlayerCore.CdgScaling`, while a song plays or is paused.
The next frame shows the change.

```csharp
// Clean look at half the screen height, with the default outline and soft shadow.
player.CdgScaling = new CdgScalingOptions { Scale = CdgScale.Medium, Style = CdgScaleStyle.Clean }
    .WithDefaultEffects();

// Back to the classic picture.
player.CdgScaling = default;
```

| Property | Type | Default | Effect |
| --- | --- | --- | --- |
| `Scale` | <xref:FatzMedia.Video.CdgScale> | `None` | How big the picture is drawn, as a whole multiple of the 216-pixel-high CD+G screen, before the GPU fits it to the screen. `None` is the classic picture. `Low` is 2x (432 high) on any screen. `Medium` is the largest multiple at or below half the screen height, at least 2x (1080p: 2x, 1440p: 3x, 4K: 5x). `High` is the largest multiple at or below the screen height (720p: 3x, 1080p: 5x, 1440p: 6x, 4K: 10x). Medium and High follow the height of the largest screen or window showing the picture, so the look is the same at any resolution. A higher scale costs more CPU, most of it for the effects. |
| `Style` | <xref:FatzMedia.Video.CdgScaleStyle> | `Pixel` | How each CD+G pixel becomes many. `Pixel` is sharp squares. `PixelArt` turns diagonal steps into clean slopes and keeps square corners. `Clean` draws crisp, even strokes with square ends and smooth curves; it needs no tuning. `AntiAliased`, `Traced` and `Curved` are older looks that `Clean` replaces (`Curved` draws the same as `Clean`); the test players do not offer them. |
| `SafetyZone` | <xref:FatzMedia.Video.CdgSafetyZone> | `Show` | Which part of the 300x216 screen is shown. `Show` is all of it, border included. `Remove` is the 288x192 picture area. `Auto` is `Remove` unless the song draws in the border, then `Show`. `Super` is the box around everything the song ever draws, fixed for the whole song. |
| `OutlineSize` | `int` 0 to 10 | 0 | Outline width around what the song draws. |
| `ShadowSize`, `ShadowDistance` | `int` 0 to 10 | 0 | How far the shadow spreads, and how far down and right it falls. A shadow is drawn when either is above 0. |
| `BevelStrength`, `BevelSize` | `int` 0 to 10 | 0 | A highlight on the top-left edges and a shade on the bottom-right ones; both must be above 0. |

Effect sizes count half CD+G pixels (10 is 5 CD+G pixels), whatever the scale. `WithDefaultEffects()` sets an
outline of 2, a shadow of 1 spread and 2 distance, and no bevel. Values above 10 are clamped.

> [!NOTE]
> The scaled picture is drawn by FatzAudio, so it needs the FatzAudio engine, which is the only engine. An
> outline, shadow or bevel needs a scale above `None`; with `Scale = None` they are not drawn.
> `PlayerCore.CdgScaling` survives a mode switch, and `Reset` takes the new configuration's value.

### The test players' Enhance setting

The WPF, Avalonia and WinForms test players show one setting, **Enhance**, with the levels Off, Low, Med and High,
plus outline, shadow and bevel sliders and a reset. It is a good model for a karaoke host that wants one control:

| Enhance | `Scale` | `Style` |
| --- | --- | --- |
| Off | `None` | `Pixel` |
| Low | `Low` | `Clean` |
| Med | `Medium` | `Clean` |
| High | `High` | `Clean` |

With Enhance Off and an outline, shadow or bevel switched on, the player draws 2x with plain pixels (`Scale = Low`,
`Style = Pixel`), because the effects need the scaled picture. The command-line switches `--cdg-enhance`,
`--cdg-scale`, `--cdg-style` and `--cdg-effects` set the starting values; see [Test players](../reference/test-players.md).

## CD+G sync offset

Some CD+G files are slightly early or late against their audio. `CdgSyncOffset` shifts only the
graphics; the audio is the clock.

```csharp
player.CdgSyncOffset = TimeSpan.FromMilliseconds(-300);   // set an exact value
TimeSpan offset = player.AdjustCdgSyncOffset(+2);         // or add two 100 ms steps
player.ResetSyncOffsets();                                // CD+G, audio and subtitle offsets to zero
```

- A positive offset shows the graphics earlier (the player draws the frame that belongs to a
  later point in the song); a negative offset shows them later.
- The value is limited to plus or minus 30 seconds.
- `AdjustCdgSyncOffset(steps)` adds `steps` times 100 milliseconds to the current value, returns
  the new offset and redraws the current frame at once.
- The offset belongs to the song. It returns to zero when the media is closed or replaced.

The test players expose this as minus and plus commands next to the transport controls; that is a good
model for a karaoke host. `AudioSyncOffset` is a separate setting for video and does not move the
graphics.

## Seeking

`SeekAsync`, `Seek` and the `Position` property move the audio and the graphics together.
The graphics are rebuilt for the target position, so the screen shows the correct words and
colours after a jump, forwards or backwards, not only a blank screen that fills in later.

```csharp
await player.SeekAsync(45.0);   // seconds
```

`SeekAsync` queues the seek on the player's own thread. If you call it for every mouse move on a
seek bar, only the newest target is kept, so it is safe to do that.

## Looping and the end of the song

`LoopMedia` works as for any other media. Set it to `false` for a karaoke queue and move to the
next song from `MediaEnded` (raised on a background thread).

## See also

- [Backgrounds](backgrounds.md): pictures and videos behind the graphics.
- [Karaoke surface](../lightweight-surfaces/karaoke-surface.md): a control that plays CD+G
  without any player code.
