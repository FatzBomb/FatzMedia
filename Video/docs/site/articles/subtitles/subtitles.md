# Subtitles

The player shows text subtitles on video. It reads them from a file next to the video (a
sidecar) or from tracks inside the video file (embedded), lets your app list and switch the
tracks, and lets the user shift the timing. MIDI/KAR lyrics are shown through the same track
mechanism. This article describes the formats, how tracks are found, how to select them and how
to style and time them.

> [!NOTE]
> Every renderer draws subtitle text. Direct3D11 uses DirectWrite; the Software and Vulkan
> renderers use the SkiaSharp rasterizer in `FatzBomb.MediaControl.Rendering.Text`, which follows
> the same layout and style rules. Fonts come from the system: when the configured family is not
> installed (Segoe UI on Linux, for example), the platform's default font is used.

## Supported formats

| Source | Formats |
| --- | --- |
| Sidecar file | SubRip `.srt`, Advanced SubStation `.ass`, SubStation `.ssa` |
| Embedded in a video file | Text tracks: SubRip, ASS, SSA, MOV text, WebVTT and plain text |

Bitmap subtitle formats (DVD, Blu-ray PGS and similar) are not supported. ASS and SSA files are
read as plain dialogue text: their own styles, colours and positions are ignored, and the text is
drawn with the style you set in `MediaPlayerConfiguration.SubtitleStyle` (see
[Styling](#styling)).

## How tracks are found

**Sidecar.** When you load a local video file, the player looks in the same folder for a file with
the same name and the extension `.srt`, `.ass` or `.ssa`. The first one found is used; if there
is more than one, `.srt` is checked first, then `.ass`, then `.ssa`. It is added to the track list
and selected. The `ignoreSidecar` argument of `LoadAsync` does not affect this: it only stops the
search for background pictures and videos.

```text
Movies\
    Film.mkv
    Film.srt      <- picked up automatically
```

**Embedded.** The text subtitle tracks inside a local video file are read in the background after
the video has opened, because reading them means scanning the whole file. The load returns
immediately, `SubtitleTracks` contains only the sidecar (if there is one), and the embedded tracks
arrive a little later. The wait depends on file size and disk speed.

When the embedded tracks arrive:

- They are added to `SubtitleTracks`.
- The first embedded track is selected and replaces an automatically selected sidecar, unless your
  app has already called `SelectSubtitleTrack` for this video (with a track or with `null`).
  The sidecar stays in the list and can be selected.
- `SubtitleTracksChanged` is raised, with the current tracks and the selected id. It is raised
  only when embedded tracks were found.

Embedded tracks are not read for videos inside archives or for network video, and sidecar
subtitles are only looked for next to local video files.

## Listing and selecting tracks

<xref:FatzBomb.MediaControl.MediaControlPlayer> exposes:

| Member | Meaning |
| --- | --- |
| `SubtitleTracks` | The tracks available now, as <xref:FatzBomb.MediaControl.Core.Subtitle.SubtitleTrack> records. |
| `SelectedSubtitleTrackId` | The selected track's id, or `null` when subtitles are off. |
| `SelectSubtitleTrack(string? id)` | Selects a track and returns `true` if it exists. `null` turns subtitles off. |
| `HasSubtitle` | `true` while a track is selected and showing. |
| `SubtitleTracksChanged` | Raised when tracks or the selection changed after the load. |

A track has three properties: `Id`, `DisplayName` and `IsEmbedded`. Ids are stable for one loaded
file: `sidecar` for the sidecar and `embedded:<stream>` for an embedded stream. The sidecar's
display name is its file name; an embedded track is named like `Embedded SUBRIP (stream 2)`.

```csharp
player.MediaLoaded += (_, _) => Dispatcher.BeginInvoke(RefreshTracks);
player.SubtitleTracksChanged += (_, e) =>
    Dispatcher.BeginInvoke(() => Show(e.Tracks, e.SelectedTrackId));

void RefreshTracks() => Show(player.SubtitleTracks, player.SelectedSubtitleTrackId);

void Show(IReadOnlyList<SubtitleTrack> tracks, string? selectedId)
{
    // Fill your picker. Add an entry for "Off" that calls SelectSubtitleTrack(null).
}

void OnTrackPicked(SubtitleTrack? track) => player.SelectSubtitleTrack(track?.Id);
```

> [!WARNING]
> `SubtitleTracksChanged` is raised on a background thread, after `MediaLoaded`. Marshal to the UI
> thread before you touch UI state. Read the tracks from the event arguments, or re-read
> `SubtitleTracks`.

### Restoring a user's choice

If you remember which track the user preferred, select it from `MediaLoaded`. An embedded id that
has not arrived yet is not in the list, so `SelectSubtitleTrack` returns `false`, but while the
background scan is still running the request is remembered and applied when the tracks arrive.
Your choice then also stops the "first embedded track" default.

```csharp
player.MediaLoaded += (_, _) => player.SelectSubtitleTrack(preferredTrackId);
```

### Turning subtitles off

`player.SelectSubtitleTrack(null)` switches subtitles off for the current video. A later load starts
again with the default selection.

## Timing

`SubtitleSyncOffset` shifts the subtitle timeline against the video and audio, for files that were
timed for another release.

```csharp
player.SubtitleSyncOffset = TimeSpan.FromMilliseconds(250);   // subtitles appear 250 ms earlier
TimeSpan now = player.AdjustSubtitleSyncOffset(-1);           // one 100 ms step later
player.ResetSyncOffsets();                                    // all offsets back to zero
```

- A positive value makes subtitles appear earlier; a negative value makes them appear later.
- The value is limited to plus or minus 30 seconds.
- `AdjustSubtitleSyncOffset(steps)` adds `steps` times 100 ms and returns the new offset.
- The offset belongs to the loaded file and returns to zero when the media is closed or replaced.

`AudioSyncOffset` moves the video against the audio clock and `CdgSyncOffset` moves CD+G graphics;
they are separate settings.

## Styling

Subtitle text is drawn by the player with one style, set when the player is created in
`MediaPlayerConfiguration.SubtitleStyle` (a <xref:FatzBomb.MediaControl.Core.Configuration.SubtitleTextStyle>).
It cannot be changed afterwards; create a new player to use another style. Colours are ARGB values
(`0xAARRGGBB`).

| Property | Default | Meaning |
| --- | --- | --- |
| `FontFamily` | `Segoe UI` | Font. |
| `FontSize` | 44 | Size, limited to 6 to 240. |
| `TextArgb` | white | Text colour. |
| `OutlineArgb` | 75 % black | Outline colour. |
| `OutlineWidth` | 1.25 | Outline width, 0 to 32. |
| `ShadowArgb` | 50 % black | Shadow colour. |
| `ShadowOffsetX` / `ShadowOffsetY` | 2 / 2 | Shadow offset, -128 to 128. |

```csharp
MediaPlayerConfiguration configuration = MediaPlayerConfiguration.Auto with
{
    SubtitleStyle = new SubtitleTextStyle
    {
        FontFamily = "Segoe UI Semibold",
        FontSize = 52,
        TextArgb = 0xFFFFFF99,
    },
};
```

MIDI/KAR lyrics have their own style (`KaraokeStyle`); see
[MIDI and KAR](../karaoke/midi-and-kar.md).

## MIDI and KAR lyrics

The lyrics of a MIDI file appear as an embedded track with the id `embedded:0` and the name
"Embedded MIDI Lyrics (stream 0)". They are selected automatically while `ShowMidiLyrics` is
`true` (the default); set it to `false` to hide only the lyrics.

## See also

- [Karaoke overview](../karaoke/overview.md)
- [MIDI and KAR](../karaoke/midi-and-kar.md)
