# FatzBomb.MediaControl.Rendering.Text

SkiaSharp plain-subtitle rasterizer for `FatzBomb.MediaControl`. The software and Vulkan
renderers use it to draw text subtitles (SRT, ASS/SSA, embedded tracks); the Direct3D11 renderer
keeps its DirectWrite rasterizer. MIDI/KAR karaoke lyrics are not drawn here: `RenderKaraoke`
returns an empty result, and lyrics on these renderers come from FatzAudio's lyric surface in a
later stage (037A).

`SkiaSubtitleRasterizer` implements Core's `ISubtitleRasterizer` and follows the Direct3D11
rasterizer's layout and drawing rules for plain cues, styled by
`MediaPlayerConfiguration.SubtitleStyle`: bold text centred near the bottom, wrapped inside 85 %
of the canvas width, with an eight-way outline and a drop shadow.

The result is the bounds of the drawn text as premultiplied BGRA8 rows on a canvas of the
requested size, the format Core and every renderer composite. Text layouts (with their glyph
runs) are cached while frames use them and the bitmap is kept while its size stays the same, so
a repeated subtitle allocates nothing; the pooled overloads write into the caller's buffer.

Fonts come from the system: the configured family when it is installed, otherwise the
platform's default (fontconfig on Linux), with per-character fallback for glyphs the family
lacks. Bold and italic are simulated when the matched typeface has no such face. Text is placed
by glyph advances without shaping, so complex scripts that need ligatures or reordering render
less well than with DirectWrite.

The package brings SkiaSharp 3.119.4 (the version Avalonia 12.1.4 uses) with its native library
for Windows and macOS, plus `SkiaSharp.NativeAssets.Linux`. Hosts need no extra step: the native
`libSkiaSharp` lands next to the application by the usual NuGet runtime-asset rules.
