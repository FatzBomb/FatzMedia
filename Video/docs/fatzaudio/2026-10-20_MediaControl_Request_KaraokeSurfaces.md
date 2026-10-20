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

**Layout.** Lines are centred, in fixed slots around the middle of the canvas: the line being
sung, and the next line below it at reduced opacity (`next_line_opacity`, default 0.65). When a
line is finished it moves up out of its slot and the next one moves in, eased over
`line_transition_ms` (default 1000). The text block is 85% of the canvas width; a line that is
too wide wraps at a word.

**Wipe.** Each syllable changes from the unsung colours to the sung ("wipe") colours as it is
sung, sweeping left to right across the syllable over its duration, so long held notes fill
slowly. `wipe_transition_ms` (default 600) caps how long a single syllable's sweep may take, so
short syllables do not flicker and long ones do not crawl.

**Pages.** A paragraph mark (`\` in `.kar`, `FA_MIDI_NEW_PARAGRAPH`) clears the page: the old
lines fade and shrink slightly while the new page fades in.

**Countdown.** Before the first syllable of a song or of a page that follows a long gap, four
arrows (▶ ▶ ▶ ▶) appear above the first line and go out one per beat, so the singer knows
when to start.

**Intro and ending pages.** Before the first lyric and after the last, the song's metadata is
shown: title, artist, then credits (`@I` lines: writer, lyricist, year), the length, and the
copyright, fading in and out. When there is room this uses two bands (title and artist at the
top, credits at the bottom).

**Lines shown.** `lines_shown` sets how many lyric lines are on screen at once (1 to about 4;
ours is 2: the line being sung and the next). A **subtitle mode** puts the lines at the bottom of
the canvas, smaller, without the intro pages and countdown, for lyrics over a video or a
music-video background.

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
| Outline colour and width, unsung and sung separately | `E0000000`, 1.75 px |
| Shadow colour, unsung and sung separately; shadow offset x, y | `A0000000`, (2, 2) |
| Next-line opacity | 0.65 |
| Line transition time | 1000 ms |
| Wipe time cap per syllable | 600 ms |
| Show countdown arrows | on |
| Show intro/ending metadata pages | on |
| Lyric lead (show a line before it is sung) | about 400 ms |
| Lines shown (1 to 4) | 2 |
| Layout mode: karaoke (centred block) or subtitle (bottom, smaller, no intro pages) | karaoke |
| Vertical position of the lyric block (centre, or a fraction of the height) | centre |
| Background | always transparent (we fill it, see Part 3) |

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
preview window, the TV output) can differ.

## Smoothness: one range, you choose the method

`smoothness` from 0 to 100 (or 0 to 10), replacing `FA_CDG_SMOOTH`:

- **0 = original.** Sharp pixels, native size (see export scale below). Nothing is invented.
- **Low:** gentle smoothing (bilinear or similar), for those who like the soft TV look.
- **Middle:** edge-directed pixel-art upscaling (xBR / xBRZ / ScaleFX family), which keeps the
  lyrics' strokes sharp while rounding their staircase edges.
- **Maximum: as close to true HD as there is a method for.** CD+G lyrics are flat-colour shapes
  on a tile grid, which suits vectorization (tracing the colour regions to curves, as in
  "Depixelizing Pixel Art" or potrace) and rendering them at the output size with antialiasing.
  If that is too slow per change, the best edge-directed upscaler with a final antialias pass.

The method behind each step is yours to choose and may change between versions. What we ask is
that the range is monotonic (higher is never rougher), there are no colour fringes (smooth in
premultiplied alpha against a transparent background, see Part 3), and the cost per changed
frame is published.

## Lighting and depth

`depth` from 0 (flat, today) to 100: a soft drop shadow under the lyric shapes, offset down and
right, and optionally a slight bevel (a light top-left edge, a darker bottom-right edge) so the
lyrics read as raised. Options: shadow colour and opacity, offset, blur radius, bevel strength
and light angle. The shadow needs room outside the shapes: grow the output by the shadow's
reach rather than clipping it (and report that margin).

## Export scale

`scale` 1x to 5x (5x is 1500x1080, full HD height): the size of the bitmap you return, before
any border or safety-zone crop. Smoothness 0 always returns 1x whatever `scale` says: there is
nothing to gain from bigger sharp pixels, and our GPU scales sharp pixels for free. Above 1x we
scale the rest of the way to the screen ourselves with bilinear filtering, which is invisible
after your upscale. This replaces asking for an exact width and height for us (our layout code
decides the final size; see Part 3). Larger scales are where smoothness and depth pay off.

## Safety zone

The CD+G screen is 300x216 with a 6-pixel border left and right and 12 pixels top and bottom;
most discs keep their graphics inside the 288x192 picture area, some draw into the border. One
option with four values, each of which changes the returned size:

- **Show:** the whole 300x216 screen, border included (today's default).
- **Remove:** only the 288x192 picture area (today's `FA_CDG_NO_BORDER`).
- **Auto:** a pre-pass over the whole file decides: if no pixel other than the background is
  ever drawn in the border area, Remove; otherwise Show. Border presets (a solid border colour)
  do not count; only tiles and scrolls do. A tolerance would help: ignore drawing in the border
  that only happens in the first few seconds (title cards), as an option.
- **Super:** a pre-pass finds the bounding box of everything ever drawn other than the
  background, over the whole song, and returns only that box (scaled as above). Fixed for the
  whole song so the picture never jumps; snapped to the tile grid (6x12) so shapes are never
  cut; with an option to keep it centred (crop the same on both sides) because asymmetric crops
  shift the lyrics off-centre. The shadow margin is added outside the box.

The pre-pass is the same walk you already do to build checkpoints, so it should cost little.
Report the chosen mode and box in `fa_cdg_info` (for Auto and Super), and recompute it when
new graphics are loaded. Discs cannot be pre-passed; there Auto and Super can fall back to Show.

## Padding: ours

Space around the graphics is layout, so we add it in our compositor (it only shrinks the
rectangle we draw into). No option needed.

# Part 3: how surfaces hand pixels to us (both kinds)

## Background: always transparent, we fill it

We keep the background transparent in the surface (`FA_CDG_TRANSPARENT_BACKGROUND_ONLY`, and the
lyric surface's background) and draw the background colour ourselves across the whole screen:
filling around a picture scaled to any window shape, under video, and with the user's own
opacity, is presentation and belongs to us. What we need:

- the background colour and changes to it: `fa_cdg_frame.background` and `background_index`,
  bumping `palette_revision`, and `FA_CDG_CHANGE_PALETTE` on the push event. That exists today:
  please keep it exactly.
- the **border** made transparent too (a flag), with its colour still reported (`border_index`),
  so in Show mode we decide whether the border is drawn, in its colour or ours.
- smoothing and depth computed in premultiplied alpha against transparent, so edges blend
  correctly over whatever we fill underneath (no dark or coloured halos).

## Hardware acceleration: in our renderer, not in FatzAudio

We do not think FatzAudio should take on a GPU dependency (Direct3D, Vulkan and Metal per
platform). Our renderers already own the GPU, and today our Direct3D11 renderer draws CD+G from
an index texture and a 16-colour palette with a shader. What makes your CPU surfaces fast enough
for us:

1. **Caller-owned destination.** `update_into(surface, time, dst, stride, &frame)`: you draw into
   our buffer. We pass a mapped GPU staging texture or one of our pooled frame buffers, so there
   is no copy between your buffer and our upload, and the frame we hand to our compositor thread
   is never written again (today `pixels` is overwritten by the next update, which forces us to
   copy every changed frame).
2. **Dirty rectangles** (as today), so a wipe uploads a few tiles, not 2 MB.
3. **INDEXED8 at smoothness 0**, which you have: we keep scaling and colouring on the GPU.
4. **Reference shaders, optional.** If smoothness or depth is too heavy on the CPU at 5x, publish
   the method (GLSL or HLSL source, or exact math) so a renderer may run the same effect on the
   GPU with the same look. We would measure your CPU cost first.

Please publish the CPU cost of an update at each scale with smoothness and depth at maximum (a
full-screen change and a typical lyric wipe).
