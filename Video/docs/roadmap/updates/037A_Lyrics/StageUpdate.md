# Stage Update

Stage: 037A - MIDI/KAR lyrics drawn by FatzAudio (the slice 3 piece left at first)
Date: 2026-11-10

## Status: implemented, pending manual smoke

With the FatzAudio engine (`--audio=fatz`), the lyrics of a MIDI or .kar song are drawn by FatzAudio's lyric
surface instead of our cue rasterizers (DirectWrite on Direct3D11, SkiaSharp on Software and Vulkan). BASS keeps
our rasterizers. Our lyric renderers stay as the fallback and for BASS until the slice 6 decision.

## Design

| Part | Where | What |
|---|---|---|
| `IKaraokeOverlaySource` (internal) | Core, `Core/Subtitle` | An audio engine that draws the open song's lyrics: `DrawsOverlay`, and `DrawOverlay(time, canvas, rentPixels)` returning a `SubtitleRasterResult`. |
| `MediaPresentationCoordinator.UpdateSubtitleOverlay` | Core | When the engine draws lyrics, its frame becomes the subtitle overlay (same time: position plus subtitle offset); the cue path is skipped. An unchanged frame in the same pooled buffer is not presented again. |
| `FatzAudioEngine.Lyrics.cs` | `FatzMedia.Video.Audio` | A `LyricSurface` on the open MIDI stream (when it has lyric events), presentation size = the 1920x1080 subtitle canvas, `RenderAhead` on, so the presentation thread only copies what changed. Each pooled buffer remembers which surface revision it holds, so a buffer that comes back (no composition holds it) gets only the change. |
| `FatzAudioMappings.ToLyricStyle` | same | FatzAudio's Classic preset (our look: white, sung yellow, black outline and shadow, cross-fade wipe) with `KaraokeStyle`'s font, text and sung colours, outline (colour, sung colour, width), shadow (colours, offset as distance and angle) and next-line opacity; pixel sizes convert through the font size to FatzAudio's em fractions. |

Why the subtitle layer: it is a premultiplied BGRA bitmap placed on a canvas that every renderer already
composites, which is exactly what the lyric surface produces (its layout gives the bitmap's place on the
presentation area). No renderer changed.

Not done: a dedicated karaoke thread (render-ahead already keeps drawing off the presentation thread), a
governor, Player options for the lyric look beyond `KaraokeStyle` (preset, wipe style, countdown, panel): FatzAudio
offers them (`LyricStyle`), and they become options when someone asks.

## Tests

`Karaoke_LyricsAreDrawnByFatzAudio_OnTheSubtitleCanvas_OnlyTheChangeWhenTheBufferComesBack` (real FatzAudio, a
generated .kar): a WAV draws no lyrics, the .kar does; the frame sits inside the 1920x1080 canvas and has visible
pixels; drawing the same moment into the same buffer reports no change; after close nothing is drawn.
`EngineDrawnLyrics_ReplaceTheCueRaster_AndAnUnchangedFrameIsNotPresentedAgain` (Portable): the coordinator uses the
engine's frame (no cue raster), presents its placement and pixels, skips an unchanged frame and clears the overlay
once when the song closes.

## Review notes (pre-PR)

Fixed: each player's overlay pool is its own (a pool shared by players could hand one player's buffer to
another, and the remembered frame revision would then be wrong); a layout that changes between reading it and
drawing is read again; outline and shadow follow `KaraokeStyle`. Known: while FatzAudio draws a song's lyrics,
other subtitle cues for that song (a sidecar file next to a .kar) are not shown.

## Manual smoke (owner)

Check 8 of `updates/037A_5/HandTests.md` (the script): a .kar with `--audio=fatz` shows its lyrics, sung words turn
yellow in time, lines scroll and pages change; the Log says `Lyrics drawn by FatzAudio (<font>)`. Compare the look
with a run without `--audio=fatz` (our renderer).
