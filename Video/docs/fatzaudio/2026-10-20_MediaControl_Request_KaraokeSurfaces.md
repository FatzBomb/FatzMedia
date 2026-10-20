# FatzBomb.MediaControl: requests for karaoke surfaces (MIDI lyrics and CD+G)

To: FatzAudio. From: FatzBomb.MediaControl, 2026-10-20.

Part 1 asks for a MIDI lyric surface. Part 2 asks for CD+G rendering options. Part 3 is about
how surfaces hand pixels to us, which applies to both. The goal for both: if we go in, it should
be the best-looking karaoke there is, not a generic renderer.

# Part 1: MIDI lyric surface

## The ask

Today FatzAudio draws CD+G for us (`fa_cdg_surface_*`: pixels at the time heard, a changed
flag and a dirty rectangle, any size and format, several surfaces per stream), but MIDI and
`.kar` lyrics only come as timed text (`fa_stream_get_midi_text`, `FA_EVENT_MIDI_TEXT`). We
would like MIDI lyrics drawn the same way, so an app shows both kinds of karaoke through one
path: create a surface, call update each frame, upload when changed.

Decided on our side: we want FatzAudio to build this. It is a plus for both sides: every FatzAudio app gets karaoke lyrics that look
finished, and our renderers get them on every platform. Our implementation is attached as the
starting point (the design reference at the end of this note, with the source files).

For us this would also fix a real gap: our own karaoke text renderer uses DirectWrite, so MIDI
lyrics only appear with our Direct3D11 renderer on Windows. Our software and Vulkan renderers
(Linux, and the fallback chain) show no MIDI lyrics at all.

Below is how ours looks and behaves. It is the bar: the result must look like our design or
better, not like a generic subtitle renderer. If you see a better way for any part, propose it.
The override list at the end is needed either way.

## A suggested API shape (mirroring CD+G)

- `fa_lyric_surface_create(stream, config, &surface)`: `config` holds the pixel format and
  size (as `fa_cdg_surface_config`) plus a style struct (below). A stream without lyrics gives a
  transparent picture.
- `fa_lyric_surface_update(surface, &frame)` at the time heard (`fa_stream_get_clock`), and
  `fa_lyric_surface_update_at(surface, time, &frame)` for our own clock, previews and video
  export. `frame` has `pixels`, `stride`, `changed`, and a dirty rectangle. Straight or
  premultiplied alpha as an option (we composite premultiplied BGRA over video or a background).
- `fa_lyric_surface_set_style(surface, &style)` at any time; the next update redraws.
- Cheap when nothing changed, like CD+G. During a wipe the picture changes every frame, so a
  tight dirty rectangle (the line being sung) matters.
- Threads and lifetime rules as for CD+G surfaces.

## Our design (for reference)

The exact design, with every number and its source line, is in
`2026-10-20_MediaControl_KaraokeDesignReference.md` (next to this note). The sources are in the
FatzBomb.MediaControl repository: `Rendering.Direct3D11/Subtitle/SubtitleTextRenderer.cs`
(drawing), `Core/Subtitle/SubtitleCue.cs` (timing model), `Core/Audio/MidiLyricExtractor.cs`
and `MidiMetadataExtractor.cs` (text to lines, pages and metadata),
`Core/Configuration/KaraokeTextStyle.cs` (options). In short:

**Layout.** A fixed 1920x1080 canvas; lines centred in an 85%-wide box, in slots 1.55 x the
font size apart around the middle: the line being sung at full size, the next line below it at
reduced opacity and scale, and the lines just sung above it fading and shrinking (scales 1.0,
0.8, 0.6; opacities 1, 0.65, 0.5, 0.25). Lines scroll up a slot as each is finished, eased.

**Wipe.** As each syllable is reached it changes from the unsung colours to the sung colours,
eased over the shorter of 600 ms and 80% of the gap to the next syllable (gaps under 150 ms cut
instantly). Ours is a colour cross-fade per syllable, not a sweep: please offer both, a
left-to-right sweep across the syllable over its duration (true karaoke) and the cross-fade.

**Pages.** A paragraph mark or a long pause (2 s) clears the page: the old page fades out
shrinking to 75%, the new one fades in from 150%.

**Countdown.** Before a line that follows a gap of more than 6 s, four arrows above it count
down the last 5 seconds, one turning green per second, then fade.

**Intro and ending.** Before the first lyric and after the last, the song's metadata is shown
(title, artist, credits from `@I` lines, length, key, tempo, copyright) on bands or a card.

**What not to copy** (section 8 of the reference lists twelve quirks): many values are
hard-coded and should be options; `LineTransitionMilliseconds` does not drive the line scroll
(1500 ms fixed); the wipe clip creeps into the next letter and flickers mid-word; wrapped lines
are wiped wrongly; outlines and shadows do not scale with smaller lines; the countdown is
clock-based, not beat-based (beat-based would be better: the MIDI tempo map is known).

**Lines shown.** How many lines are on screen: sung lines kept above the current one and
upcoming lines below it (ours: up to two above, one below). A **subtitle mode** puts one or two
lines at the bottom of the canvas, smaller, without the intro pages and countdown, for lyrics
over a video or a music-video background.

**Text cleanup** (worth having in `fa_midi_text` whatever you decide): chord lines (Tune1000
`%...`, Solton `<...>`) are not lyrics; `/` and `\` are line and page marks wherever they appear
in a fragment, not only at the start.

## What must be overridable (our current options, ARGB colours)

| Option | Our default |
|---|---|
| Font family, or a font file / memory blob (cross-platform: no reliance on system fonts) | Segoe UI |
| Font size (in pixels at the surface size, or as a fraction of its height) | 72 px at 1080p |
| Bold / weight | bold |
| Unsung text colour | white `FFFFFFFF` |
| Sung (wipe) text colour | yellow `FFFFFF00` |
| Outline, shadow and bevel: the shared effects block (Part 2), with unsung and sung colours | outline `E0000000`, shadow `A0000000` |
| Next-line opacity | 0.65 |
| Line scroll time; page transition time (separate options) | 1500 ms; 1000 ms |
| Wipe style: sweep or cross-fade; time cap per syllable | (new); 600 ms |
| Countdown: on/off, count, colours, clock or beat based | on, 4, red to green, clock |
| Show intro/ending metadata pages | on |
| Lyric lead (show a line before it is sung) | about 400 ms |
| Lines shown: sung lines kept above and upcoming lines below (0 to 2 each) | 2 above, 1 below |
| Layout mode: karaoke (centred block) or subtitle (bottom, smaller, no intro pages) | karaoke |
| Vertical position of the lyric block (centre, or a fraction of the height) | centre |
| Background panel: colour, opacity, corner radius, padding around the lines, on/off (a band behind the lyric block that keeps them readable over busy video; transparent around it) | off |
| Design preset (see below), which every option above overrides | Classic |

**Design presets.** A few complete looks the app can pick by name, each setting every option
above, with individual options overriding the preset: for example **Classic** (ours: white,
yellow wipe, black outline and shadow), **Modern** (a slimmer font, a soft panel, a gradient
wipe), **Neon** (glow instead of outline), **Minimal** (no outline, a light shadow) and
**Subtitle** (bottom placement, two lines, smaller). These are suggestions; the presets should
all look designed, not like defaults.

Text quality: antialiased glyphs with subpixel positioning (ours is DirectWrite), proper shaping
(HarfBuzz or similar) and kerning. Nice to have: font fallback for glyphs the chosen font lacks (CJK), and right-to-left text.

## Prerequisite: text encoding

A lyric surface makes the encoding gap visible: Shift-JIS `.kar` files (common in Japanese
karaoke) come out garbled today (`dec_midi.c:340-358`). Detecting Shift-JIS before the
Windows-1252 fallback, or letting the app pass a code page, would cover most files.

## Tempo, for the record

We checked: CD+G surfaces follow `fa_stream_get_clock`, which runs on the stream's own timeline
(at tempo 1.25 it advances 1.25 s per second), and `fa_midi_text` times are on the same timeline.
So graphics and lyrics follow tempo, pitch, seeks and loops with no work on our side. A lyric
surface would keep that property for free; the only tempo-dependent piece is the wipe-time cap
and transitions, which should stay in wall-clock milliseconds (a fast song should not make the
line scroll animation faster).

# Part 2: CD+G rendering options

Your decoder is exact (it matched ours on every pixel and palette entry; see our review note).
These ask for presentation quality on top of it. All are surface options, so each surface (a
preview window, the TV output) can differ, and all can change while playing.

## Output scale and internal quality are separate

- **Output scale** (`scale`, 1x up to about 10x, 3000x2160 for 4K height): the size of the
  bitmap you return, before crop, margin and padding. Smoothness 0 with no effects always
  returns 1x (sharp pixels gain nothing from being bigger; our GPU scales them for free).
- **Internal quality:** you draw at whatever internal resolution the smoothness level and the
  effects need (supersampling) and average down to the output size. That is what solves the
  thin-letter problem: an "I" one or two CD+G pixels wide cannot carry a bevel or a fine
  outline at 1x, but drawn at 8x inside and averaged down, the effect is a fraction of a pixel
  that shows as a soft, correct edge even in a small output. So effects look right at every
  output scale; a bigger output just shows more of their detail. The internal resolution is
  your choice (report it, and its cost).

## Smoothness: 0 to 10

One quality number; you choose the method behind each level and may change it between versions.
It must be monotonic (higher is never rougher), free of colour fringes, and its cost per changed
frame published.

- **0: original.** Sharp pixels, nothing invented.
- **1-4: soft.** From a light smoothing up to bilinear-like softness at the output size.
- **5-8: sharp and smooth.** Edge-directed pixel-art upscaling (xBR, xBRZ, ScaleFX family):
  strokes stay crisp while their stair-steps round off.
- **9-10: true HD, as far as there is a method.** CD+G lyrics are flat-colour shapes on a tile
  grid, which suits vectorization: trace each colour region to curves (as in "Depixelizing Pixel
  Art", or potrace) and render the curves at the output size with anti-aliasing. If that is too
  slow per change, the best edge-directed upscaler with a final anti-alias pass.

## Edge blend: on or off, separate from smoothness

`edge_blend` on/off, applied at the output size whatever the smoothness and scale: each edge
pixel takes a colour and alpha computed from its neighbours, so stair-step edges are
anti-aliased without resizing anything. For example, a pixel whose top, left and top-left
neighbours are orange and whose others are background becomes part-orange with matching
coverage. Known methods do exactly this: morphological anti-aliasing (MLAA, SMAA: find the
stair-step edges and compute the coverage of the line they approximate). A plain blur is not
it: it softens strokes instead of shaping their edges. At smoothness 0 and 1x it gives an
anti-aliased original-size picture.

## Effects: shared by CD+G and MIDI lyrics

One effects block, used by both surfaces (for CD+G it applies to the lyric shapes, everything
that is not background or transparent; for MIDI lyrics to the text, with separate colours for
unsung and sung text). Effects are drawn in this order: shadow, outline, shape or text, bevel.

| Effect | Options |
|---|---|
| Outline (stroke around the shapes; with fade 1.0 it is a glow) | on/off, colour, opacity, size 0.0-1.0, fade 0.0-1.0 |
| Shadow (offset copy under the shapes) | on/off, colour, opacity, size 0.0-1.0 (spread), distance 0.0-1.0, angle, fade 0.0-1.0 |
| Bevel (light upper-left edge, dark lower-right edge) | on/off, highlight and shade colours, strength, size 0.0-1.0, light angle, fade 0.0-1.0 |

**Size and distance, 0.0 to 1.0.** Not in pixels: 1.0 is the largest size that still looks
designed, 0.0 the smallest visible one, each defined by you relative to the shapes they apply to
(the stroke width or letter height: for CD+G a typical lyric stroke, for MIDI lyrics the font
size). So the same setting looks the same on CD+G and on text, at every output scale and font
size. Publish what 0.0 and 1.0 correspond to.

**Fade, 0.0 to 1.0** (each effect that has a width):
- **0.0:** no fade: the effect is solid out to its full width.
- **0.5:** solid for the inner half, then fades to transparent by the outer edge.
- **1.0:** fades over the whole width, from the shape's edge outwards (a soft glow for an
  outline, a soft shadow).
The fade starts at the outer part of the effect, so 1.0 - fade is the solid share. Ease the
fade (smooth, not linear), so its end is invisible.

Effects need room outside the shapes: the built-in margin (below) grows to their reach.

## Margin, padding and the safety zone

The returned bitmap is laid out, from the outside in:

1. **Padding** (`padding`, CD+G pixels, default 0): empty transparent space around everything,
   so the bitmap is exactly the size we lay out, and the padding scales with the picture. It is
   arithmetic, not drawing, so it costs you nothing.
2. **Built-in margin**, always at least 1 CD+G pixel and never 0, separate from padding: room
   for smoothing to anti-alias the outermost edge and for the shadow and blur to spread (grown
   automatically to the effects' reach). Transparent.
3. **The graphics**, cropped by the safety zone mode:
   - **Show:** the whole 300x216 screen, border included (today's default).
   - **Remove:** only the 288x192 picture area (today's `FA_CDG_NO_BORDER`).
   - **Auto:** a pre-pass over the whole file decides: if nothing other than the background is
     ever drawn in the border area, Remove; otherwise Show. Border presets (a solid border
     colour) do not count, only tiles and scrolls. Option: ignore border drawing in the first
     few seconds (title cards).
   - **Super:** a pre-pass finds the bounding box of everything ever drawn other than the
     background, over the whole song, and returns only that box. Fixed for the whole song, so
     the picture never jumps; snapped to the 6x12 tile grid so shapes are never cut; option to
     keep it centred (the same crop on both sides), since an asymmetric crop moves the lyrics
     off-centre.

   The pre-pass is the same walk you already do to build checkpoints. Report the chosen mode
   and box in `fa_cdg_info`. Discs cannot be pre-passed: there Auto and Super fall back to Show.

The final size is therefore known before drawing: (crop size + 2 x margin + 2 x padding) x
render scale. See Part 3 for how we learn it and how changes are signalled.

# Part 3: how surfaces hand pixels to us (both kinds)

## Background: always transparent, we fill it

Both surfaces keep their background transparent (`FA_CDG_TRANSPARENT_BACKGROUND_ONLY` for CD+G,
and for lyrics unless a design asks for a panel, see Part 1), and we draw the background colour
across the whole screen ourselves. Filling around a picture scaled to any window shape, under
video, and with the user's opacity, is presentation. What we need:

- the background colour and its changes: `fa_cdg_frame.background` and `background_index`,
  bumping `palette_revision`, and `FA_CDG_CHANGE_PALETTE` on the push event. That exists today;
  please keep it exactly.
- the **border** made transparent too (a flag), with its colour still reported (`border_index`),
  so in Show mode we decide whether and how the border is drawn.
- smoothing, edge blend and effects computed in premultiplied alpha against transparent, so edges blend
  correctly over whatever we fill underneath (no dark or coloured halos).

## Drawing into our buffer, with sizes that can change

Today `pixels` belongs to the surface and is overwritten by the next update, so we must copy
every changed frame before handing it to our compositor thread. We ask for:

- `fa_cdg_surface_get_layout(surface, &layout)`: the output width and height (as above), the
  offset of the 300x216 screen inside it, and a `layout_revision`. Valid once the pre-pass is
  done (before the first update for files).
- `fa_cdg_surface_update_into(surface, time, dst, dst_stride, dst_width, dst_height, &frame)`:
  draw into our buffer. If our buffer is not the current layout size it returns
  `FA_ERROR_BUFFER_TOO_SMALL` (or a size-mismatch result) without drawing, and we reallocate.
- Live adjustment: changing smoothness, effects, scale, safety mode, margin or padding on a
  surface bumps `layout_revision` when the size changes and makes the next update a full
  redraw. Each frame reports `layout_revision`, so our render thread notices on the next frame,
  reallocates from the pool, and carries on; no extra callbacks needed. Same for the lyric
  surface (font size, lines shown or mode can change its size).
- Dirty rectangles as today, in output pixels, so a wipe uploads a few tiles.
- With our own buffers, a frame we hand to the compositor is never written again, and when our
  buffer is a mapped GPU upload texture there is no copy at all.

## Hardware acceleration

FatzAudio already has per-platform code for audio output, but a GPU path is a different kind of
dependency: the result must end up on our device (our Direct3D11 device on Windows, our Vulkan
device on Linux, Metal on Apple), so either you render on a device we hand you, or you render on
your own and share the texture across devices with synchronization and device-loss handling on
both sides. And our software renderer, and any app without a GPU, still needs the CPU path.

So we suggest, in order:

1. **CPU path first, with numbers.** Implement smoothness, edge blend, effects and scale on the CPU and
   publish the cost of a full-screen change and of a typical lyric wipe at each smoothness level,
  with effects on,
   and at 4x, 8x and 10x. Dirty rectangles keep most updates small; a full repaint happens only
   on clears and scrolls. Multithread the full repaint (rows or tiles) if needed.
2. **If the numbers show the CPU cannot keep up** at high smoothness and scale, a GPU path that
   runs on a device the app passes in (Direct3D11 device and context, Vulkan device and queue,
   Metal device), drawing into a texture the app owns. Same look as the CPU path (shared math,
   compared in tests). This is how FFmpeg's hardware frames and Skia work with an app's device,
   and it avoids cross-device sharing.
3. Either way, the method stays documented, so a renderer could run it as its own shader.

Please publish the CPU numbers before deciding on 2.
