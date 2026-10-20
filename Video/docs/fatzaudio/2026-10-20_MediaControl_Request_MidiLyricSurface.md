# FatzBomb.MediaControl: request for a MIDI karaoke lyric surface

To: FatzAudio. From: FatzBomb.MediaControl, 2026-10-20.

## The ask

Today FatzAudio draws CD+G for us (`fa_cdg_surface_*`: pixels at the time heard, a changed
flag and a dirty rectangle, any size and format, several surfaces per stream), but MIDI and
`.kar` lyrics only come as timed text (`fa_stream_get_midi_text`, `FA_EVENT_MIDI_TEXT`). We
would like MIDI lyrics drawn the same way, so an app shows both kinds of karaoke through one
path: create a surface, call update each frame, upload when changed.

For us this would also fix a real gap: our own karaoke text renderer uses DirectWrite, so MIDI
lyrics only appear with our Direct3D11 renderer on Windows. Our software and Vulkan renderers
(Linux, and the fallback chain) show no MIDI lyrics at all.

Below is how ours looks and behaves. Take it as a reference design: use it, or tell us what
you would do differently. What we need either way is the override list at the end.

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
| Vertical position of the lyric block (centre, or a fraction of the height) | centre |
| Background: transparent, or a colour with opacity (as `FA_CDG_BACKGROUND_COLOR` / `_OPACITY`) | transparent |

Nice to have: font fallback for glyphs the chosen font lacks (CJK), and right-to-left text.

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
