# Karaoke overview

FatzMedia.Video plays the common karaoke formats with the same player you use for
everything else: CD+G graphics with an audio file, CD+G inside an archive, MIDI and KAR files
with lyrics, and ordinary video with subtitles. You load the file, the library finds the pieces
that belong together, and the result is shown on your `MediaSurface`. This article lists what is
supported and points to the article for each topic.

## What the library can play

| Format | What you load | What the user sees | Article |
| --- | --- | --- | --- |
| CD+G with MP3 or other audio | `song.mp3` with `song.cdg` in the same folder, or either of the two files | The CD+G graphics, in sync with the audio | [CD+G](cdg.md) |
| CD+G alone | `song.cdg` with no audio next to it | The graphics only, without sound | [CD+G](cdg.md) |
| CD+G in an archive | A `.zip` (or another archive type) that holds the audio and the `.cdg` | Same as above | [CD+G](cdg.md) |
| MIDI and KAR lyrics | `.kar`, `.mid`, `.midi` or `.rmi` | Synthesised music with the lyrics wiping from white to yellow as they are sung | [MIDI and KAR](midi-and-kar.md) |
| Video karaoke | A video file with `.srt`, `.ass` or `.ssa` next to it, or with subtitle tracks inside | The video with the subtitle text on top | [Subtitles](../subtitles/subtitles.md) |
| A picture or video behind the lyrics | An image or video with the same name as the song | The background is drawn behind the CD+G graphics or the lyrics | [Backgrounds](backgrounds.md) |

You do not tell the player which kind of file you are loading. `LoadAsync` looks at the file
(and at the files next to it, or inside it, if it is an archive) and works out the rest. After
the load, `MediaControlPlayer.Content` tells you what was found, for example
`MediaContent.Audio | MediaContent.Cdg`, and `HasCdg`, `HasVideo` and `HasSubtitle` give the
same answer as single flags.

```csharp
if (await player.LoadAsync(@"C:\Karaoke\Artist - Song.mp3"))
{
    // player.HasCdg is true when a matching Artist - Song.cdg was found.
    // player.Content shows exactly what is playing.
}
```

## Two ways to put karaoke on screen

**Your own player.** Create a <xref:FatzMedia.Video.MediaControlPlayer>, attach it to a
`MediaSurface` and load files into it. You decide the layout, the transport controls and the
settings: sync offsets, CD+G opacity, a MIDI soundfont, the lyric font and colours, a second
screen. Use this for a full karaoke or media player.

**A ready-made karaoke control.** `KaraokeSurface` (WPF, Avalonia and WinForms) is a control
that creates and owns its own player and shows CD+G or MIDI/KAR on a centred 4:3 area. Set
`Source` and it plays. Use it when you want karaoke in one corner of a window without writing
any player code. See [Karaoke surface](../lightweight-surfaces/karaoke-surface.md).

## Things to know before you start

> [!NOTE]
> Every renderer draws MIDI/KAR lyrics, countdown arrows and title cards. Direct3D11 uses
> DirectWrite; the Software and Vulkan renderers (and so every Linux player) use SkiaSharp,
> which follows the same layout, colours and timing. Fonts come from the system, so on a machine
> without the configured family (Segoe UI by default) the default font is used.

- **Audio.** Audio is played through BASS, with FFmpeg as a fallback for formats a BASS plug-in
  would otherwise handle. MIDI and KAR need the BASS MIDI plug-in (`bassmidi.dll`, or
  `libbassmidi.so` on Linux) next to the other native libraries; without it those extensions are
  not accepted. CD+G itself needs no extra plug-in, only an audio decoder for the song's audio
  format.
- **Threads.** Player events (`MediaLoaded`, `MediaEnded`, `SubtitleTracksChanged` and others) are
  raised on background threads. Marshal to the UI thread before touching UI state.
- **Offsets are per song.** The CD+G and subtitle sync offsets go back to zero when the media is
  closed or replaced.
- **Audio CDs and CD+G discs.** The library has no disc or drive API, and this guide does not
  cover playing from a disc. Copy the disc's tracks and graphics to files first and load those.

## Where to go next

- [CD+G](cdg.md): pairs, archives, visuals, sync offset and seeking.
- [MIDI and KAR](midi-and-kar.md): lyrics, soundfonts and lyric styling.
- [Backgrounds](backgrounds.md): pictures and videos behind the karaoke.
- [Subtitles](../subtitles/subtitles.md): sidecar and embedded tracks for video karaoke.
- [Karaoke surface](../lightweight-surfaces/karaoke-surface.md) and
  [Background surface](../lightweight-surfaces/background-surface.md): the ready-made controls.
