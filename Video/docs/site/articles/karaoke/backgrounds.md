# Backgrounds

A karaoke song can play in front of a picture or a looping video. The player draws the
background first and the CD+G graphics or the lyrics on top. You can let the player find a
background by file name, pass one yourself, or give it a list to choose from. This article
describes the detection rules, the explicit options and how a background video behaves.

## Which songs get a background

A background is available behind **audio, CD+G and MIDI/KAR** media. It is not used behind a
video file or when the file itself is an image. Audio-only players (`MediaPlayerMode.Audio`) have
no picture and ignore backgrounds.

## Automatic detection

By default the player looks for a background every time you load a file.

**Loose files.** It looks in the same folder for files with the same name as the song and an
image or video extension.

```text
Karaoke\
    Artist - Song.mp3
    Artist - Song.cdg
    Artist - Song.jpg      <- background (image)
    Artist - Song.mp4      <- also a candidate (video)
```

- Image extensions: `.bmp`, `.gif`, `.jpg`, `.jpeg`, `.png`, `.tif`, `.tiff`.
- Video extensions: the video extensions FFmpeg supports, such as `.mp4`, `.mkv`, `.avi`, `.mov`,
  `.webm`, `.wmv`. They are known only after the FFmpeg libraries have loaded.
- The match is on the name without extension. On Windows and macOS case does not matter. On Linux
  the name must match exactly; only the extension is case-insensitive.

**Archives.** For a `.zip` or other archive, the candidates are every image and video inside the
archive (whatever their names) and every file next to the archive that has the archive's name, as
above. A background packed with the song therefore needs no naming convention.

**Several candidates.** If more than one background is found, one is picked at random on each
load. Put several images with the same name base in the archive, or beside the song, and the song
gets a different one each time it is played. Images and videos are picked from the same pool.

```csharp
// song.zip contains song.mp3, song.cdg, bg1.jpg, bg2.jpg and loop.mp4.
// One of the three backgrounds is chosen when the file is loaded.
await player.LoadAsync(@"C:\Karaoke\song.zip");
```

### Switching detection off

| How | Effect |
| --- | --- |
| `player.AutoBackground = false` | No sidecar or archive search for any later load. Default is `true`. |
| `LoadAsync(path, ignoreSidecar: true)` | No search for this load only. |

Both leave explicitly supplied backgrounds working (next section).

> [!NOTE]
> `ignoreSidecar` controls only the background search. A `.cdg` next to its audio file, and a
> subtitle file next to a video, are still found.

## Explicit backgrounds

Pass the background with the load call. The overloads of `LoadAsync` (and `Load`) that take a
background let you choose a file, a list of files, or a `MediaInputSource`.

```csharp
// One file: an image or a video.
await player.LoadAsync(@"C:\Karaoke\song.zip", @"C:\Backgrounds\stage.mp4");

// A list: one existing image or video is picked at random for this load.
string[] pool = [@"C:\Backgrounds\a.jpg", @"C:\Backgrounds\b.jpg", @"C:\Backgrounds\loop.mp4"];
await player.LoadAsync(@"C:\Karaoke\song.zip", pool);

// An in-memory colour or picture.
await player.LoadAsync(
    MediaInputSource.FromFile(@"C:\Karaoke\song.zip"),
    MediaInputSource.FromColor(0xFF102030));
```

The WPF, Avalonia and WinForms `Player` classes add overloads that take an `ImageSource` or
`Bitmap` of your own as the background.

Things to know:

- An explicit background is used only when the automatic search is off (`AutoBackground = false`
  or `ignoreSidecar: true`) or finds nothing. If the player finds a background by name, that one
  wins over the one you passed.
- A missing file, or a file that is neither an image nor a video, is skipped without an error.
  Paths in a list that do not qualify are skipped and the rest are used.
- An explicit background cannot be used behind a video file.
- The background belongs to the loaded item. The next `LoadAsync` replaces it, and `Close()`
  removes it.

## Background video

A video background is silent and its audio is never decoded. It behaves like this:

- It **loops** for as long as the song plays, and restarts from its first frame at the end of
  each pass.
- It starts when you press **play**, pauses on **pause**, and goes back to its beginning on
  **stop**. Loading a song shows the first frame of the video until play is called.
- It is not synchronised with the song position. It is decoration, with its own timeline.
- It is decoded for as long as it plays, so prefer modest resolutions for loops.

An image background is just a still picture and costs nothing while the song plays.

## Making the background visible

CD+G graphics normally fill the whole frame with their background colour, which would hide the
picture. Lower `CdgVisuals.BackgroundOpacity`, or switch `TransparentBackground` on, so the
background shows through. See [CD+G](cdg.md#changing-how-the-graphics-look).

```csharp
player.CdgVisuals.BackgroundOpacity = 0;   // only the graphics are drawn over the background
```

MIDI lyrics are drawn as outlined text over the background, so they need no change.

## Backgrounds and the karaoke surface

`KaraokeSurface` loads songs with `ignoreSidecar: true`, so it never shows a background behind
the song. To combine a background with the ready-made controls, place a `BackgroundSurface`
next to it (see [Background surface](../lightweight-surfaces/background-surface.md)), or use your
own player.
