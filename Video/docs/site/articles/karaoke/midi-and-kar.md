# MIDI and KAR lyrics

MIDI files can carry the words of a song as timed text events. The player synthesises the music
with a soundfont and shows the lyrics as karaoke: each syllable is highlighted as it is sung, and
the text scrolls and pages along with the song. This article covers what the user sees, how to
turn lyrics on and off, how to choose a soundfont and how to change the lyric style.

## What you need

- A MIDI file with the extension `.kar`, `.mid`, `.midi` or `.rmi`. These extensions are accepted
  only when the audio engine (FatzAudio) is running. Without it `LoadAsync` returns `false` for
  these files.
- A soundfont, or the system one. See [Soundfonts](#soundfonts).
- Any renderer, to see the words: Direct3D 11 draws them with DirectWrite, the Software and Vulkan
  renderers with SkiaSharp (system fonts; the default font when Segoe UI is not installed).

MIDI files are loaded like any other media:

```csharp
PlayerCore player = await Task.Run(() =>
    new PlayerCore(platformFactory, MediaPlayerConfiguration.Auto, nativeLibraryPaths));

await player.LoadAsync(@"C:\Karaoke\song.kar");
await player.PlayAsync();
```

Pitch, tempo, volume, normalisation, loop and seeking work for MIDI as they do for other audio.

## What the user sees

The lyrics come from the lyric events and text events in the file. The usual karaoke file
conventions for line breaks and screen clears (Soft Karaoke `/` and `\`, Tune1000 and Solton
files) are honoured, and long passages are split into lines of about 42 characters.

- **Wipe.** Words you have not reached yet use the normal text colour (white by default). When
  the song reaches a syllable it changes to the wipe colour (yellow by default), with a short
  transition. Highlighting runs 400 ms ahead of the note, so the singer has a moment to prepare.
- **Lines and pages.** Up to five lines are visible: two sung lines above, the line being sung
  in the middle, and two coming up below. Lines further from the middle are smaller and fainter.
  At a long pause in the singing (about two seconds or more) and where the file clears the
  screen, the old page fades out and the next page fades in.
- **Countdown.** When there is a gap of more than six seconds before a line (including before the
  first line), four arrows appear above it for the last five seconds. They turn from red to green
  one per second and fade out as the singing starts.
- **Title card.** The title, artist, writer and lyricist, year, length, key, tempo, time
  signature and copyright are read from the file when it has them. They are shown as an intro
  card that fades in one second after the start, and again as an end card after the last line,
  if at least three seconds of the song remain. If the file has no usable information, the intro
  uses the file name and no end card is shown. When the first line starts within the first eight
  seconds, the information is shown as a band at the top and one at the bottom instead of a
  centred card, so it does not hide the lyrics.
- **End.** The last lyrics fade out about five seconds after the final syllable.

A MIDI file without any lyric or text events plays as music only.

## Turning lyrics on and off

`ShowMidiLyrics` is `true` by default. Set it to `false` to hear the music without the words.
You can change it at any time, also while the song plays.

```csharp
player.ShowMidiLyrics = false;   // hide the lyrics
player.ShowMidiLyrics = true;    // show them again
```

`ShowMidiLyrics` only affects MIDI lyrics. SRT and ASS subtitles of a video are not touched.

The lyrics are an ordinary subtitle track (`SubtitleTracks` lists it with the id `embedded:0` and
the name "Embedded MIDI Lyrics (stream 0)"), and `HasSubtitle` is `true` while they are selected.
See [Subtitles](../subtitles/subtitles.md) for the track API.

## Soundfonts

FatzAudio turns the notes into sound with a soundfont. It selects one when the player starts
audio:

1. The path you set in `MediaPlayerConfiguration.MidiSoundfontPath`. `.sf2`, `.sf3` and `.dls`
   files are used as they are.
2. If that is not set, the operating system's General MIDI bank where there is one: `gm.dls` on
   Windows, the system bank on macOS, an installed `.sf2` on Linux.

There is no search of application folders. Ship a soundfont with your app and set
`MidiSoundfontPath` if you cannot rely on the system one.

```csharp
MediaPlayerConfiguration configuration = MediaPlayerConfiguration.Auto with
{
    MidiSoundfontPath = Path.Combine(AppContext.BaseDirectory, "soundfonts", "GeneralUser.sf2"),
};
```

> [!NOTE]
> Each player loads its own soundfont from its `MidiSoundfontPath` when it starts.

## Lyric style

`MediaPlayerConfiguration.KaraokeStyle` takes a <xref:FatzMedia.Video.Core.Configuration.KaraokeTextStyle>.
It is fixed when the player is created; to change it, create a new player. Colours are ARGB
values (`0xAARRGGBB`).

| Property | Default | Meaning |
| --- | --- | --- |
| `FontFamily` | `Segoe UI` | Font of the lyrics. |
| `FontSize` | 72 | Size, limited to 6 to 240. |
| `TextArgb` / `WipeTextArgb` | white / yellow | Text colour before and after a syllable is sung. |
| `OutlineArgb` / `WipeOutlineArgb` | 88 % black | Outline colour before and after. |
| `OutlineWidth` / `WipeOutlineWidth` | 1.75 | Outline width, 0 to 32. |
| `ShadowArgb` / `WipeShadowArgb` | 63 % black | Shadow colour before and after. |
| `ShadowOffsetX` / `ShadowOffsetY` | 2 / 2 | Shadow offset, -128 to 128. |
| `LineTransitionMilliseconds` | 1000 | Duration of page fades and line scrolling, 0 to 4000. 0 turns the animation off. |
| `WipeTransitionMilliseconds` | 600 | How long one syllable takes to change colour, 0 to 2000. It is shortened where syllables follow each other quickly. |
| `NextLineOpacity` | 0.65 | Opacity of the lines that are still to come, 0 to 1. |

```csharp
MediaPlayerConfiguration configuration = MediaPlayerConfiguration.Auto with
{
    KaraokeStyle = new KaraokeTextStyle
    {
        FontFamily = "Segoe UI",
        FontSize = 64,
        WipeTextArgb = 0xFF00E5FF,   // cyan
        NextLineOpacity = 0.5f,
    },
};
```

Values outside the limits are clamped. Video subtitles use the separate `SubtitleStyle`; see
[Subtitles](../subtitles/subtitles.md).

> [!NOTE]
> `KaraokeSurface` creates its player itself and does not take a configuration, so it always uses
> the default style and the automatically chosen soundfont. If you need to change either, use your
> own <xref:FatzMedia.Video.PlayerCore> with a `MediaSurface`.

## Sync

There is no separate MIDI offset. The lyrics are a subtitle track, so `SubtitleSyncOffset` and
`AdjustSubtitleSyncOffset` move them against the music (positive values show the words earlier).
The limit is plus or minus 30 seconds and the value is reset when the song is closed or replaced.

## See also

- [Karaoke overview](overview.md)
- [Backgrounds](backgrounds.md): a picture or video behind the lyrics.
- [Karaoke surface](../lightweight-surfaces/karaoke-surface.md): a ready-made control for KAR and CD+G.
