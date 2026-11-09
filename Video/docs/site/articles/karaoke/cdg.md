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
