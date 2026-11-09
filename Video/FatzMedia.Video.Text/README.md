# FatzMedia.Video.Text

SkiaSharp subtitle and karaoke text rasterizer for `FatzMedia.Video`. The software and
Vulkan renderers use it to draw text subtitles (SRT, ASS/SSA, embedded tracks) and MIDI/KAR
karaoke lyrics; the Direct3D11 renderer keeps its DirectWrite rasterizer.

`SkiaSubtitleRasterizer` implements Core's `ISubtitleRasterizer` and follows the Direct3D11
rasterizer's layout and drawing rules, styled by `MediaPlayerConfiguration.SubtitleStyle` and
`KaraokeStyle`:

- plain cues: bold text centred near the bottom, wrapped inside 85 % of the canvas width, with
  an eight-way outline and a drop shadow;
- karaoke: the scrolling five-line window, the two-pass syllable wipe (sung, then the current
  syllable blended from the unsung to the sung colours), countdown arrows, page fades, and the
  intro card, split title bands and end card.

The result is the bounds of the drawn text as premultiplied BGRA8 rows on a canvas of the
requested size, the format Core and every renderer composite. Text layouts (with their glyph
runs) are cached while frames use them and the bitmap is kept while its size stays the same, so
a steady karaoke wipe allocates nothing; the pooled overloads write into the caller's buffer.

Fonts come from the system: the configured family when it is installed, otherwise the
platform's default (fontconfig on Linux), with per-character fallback for glyphs the family
lacks. Bold and italic are simulated when the matched typeface has no such face. Text is placed
by glyph advances without shaping, so complex scripts that need ligatures or reordering render
less well than with DirectWrite.

The package brings SkiaSharp 3.119.4 (the version Avalonia 12.1.4 uses) with its native library
for Windows and macOS, plus `SkiaSharp.NativeAssets.Linux`. Hosts need no extra step: the native
`libSkiaSharp` lands next to the application by the usual NuGet runtime-asset rules.
