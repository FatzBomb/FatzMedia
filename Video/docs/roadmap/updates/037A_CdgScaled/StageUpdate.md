# Stage Update

Stage: 037A - Scaled CD+G drawn by FatzAudio (the slice 3 piece left at first)
Date: 2026-11-10

## Status: implemented, pending manual smoke (opt-in; the classic picture stays the default)

With the FatzAudio engine and `MediaPlayerConfiguration.CdgScaling.Enabled` (test players: `--cdg-scale` or
`--cdg-scale=<0..10>` for the smoothness, `--cdg-effects` for an outline and shadow), CD+G is drawn by FatzAudio
in full colour at a whole-number multiple of the screen, smoothed, instead of the classic 300x216 picture
scaled up on the GPU. BASS ignores the option.

## Design

| Part | Where | What |
|---|---|---|
| `CdgScalingOptions`, `CdgSafetyZone` (public, new) | Core `Api` | `Enabled` (off), `Smoothness` 0-10, `EdgeBlend`, `SafetyZone` (Show, Remove, Auto, Super), `Effects`. |
| `MediaPlayerConfiguration.CdgScaling` (public, new) | Core | The option. |
| `IKaraokeOverlaySource` (internal, renamed from `IKaraokeLyricsSource`) | Core | Now also offered by the CD+G engine; the coordinator takes the CD+G engine's picture before the audio engine's lyrics. |
| `FatzAudioCdgEngine.Scaled.cs` | `FatzMedia.Video.Audio` | A second, BGRA surface on the same carrier with FatzAudio's presentation (scale, zone, smoothness, edge blend) and effects. Drawn on the presentation thread at the position the indexed surface last rendered (no render-ahead: a late frame would have no notice that a newer one is ready). The indexed surface keeps running for the wash and the change notices; the snapshot then carries the wash and no pixels, so renderers draw the wash and the overlay. The one-CD+G-pixel transparent margin is cut off, so the picture fits the 1920x1080 canvas (5x, 1500x1080, at 1080 high with the border). |

Why the subtitle layer again: a premultiplied BGRA bitmap on a canvas, which every renderer composites. No renderer
or shader changed. The canvas is 1920x1080, so the picture is drawn for 1080 high and scaled by the renderer to the
window: crisp up to about 1080p windows, softer on a 4K window (a presentation size that follows the window is a
later step if it is wanted).

Not done (in the original slice 3 list): a dedicated karaoke thread and governor (full redraws with effects cost
15-25 ms at 1080 high, on the presentation thread, only when the picture changes), padding and per-effect settings,
linear sampling with mips (the renderers sample the subtitle layer as they do for subtitles).

## Tests

`Scaled_ThePictureIsTheOverlay_TheSnapshotOnlyTheWash` (real FatzAudio): a classic engine draws no overlay; a
scaled one does, its snapshot has no pixels and the same wash as the classic one, the picture is 1500x1080 at 210
on the canvas with visible pixels, an unchanged position reports no change, a later one does. Option parsing for
`--cdg-scale` and `--cdg-effects`.

## Manual smoke (owner)

Check M6b of the hand-test script (`scripts/smoke-037A.ps1`): an MP3+G with `--audio=fatz --cdg-scale=4
--cdg-effects` draws smooth, outlined graphics in time; seek, pause and loop keep the picture right; the wash
behind it is the song's background colour.
