# FatzAudio to MediaControl / FatzMedia, 2026-11-03 (as received)

Your rc.1 reply is applied (PR 194): the Shift-JIS "garbled" line is dropped, the Linux SONAME is
`libfatzaudio.so.1`, the K2 "nothing new" contract (`FA_OK` + `dirty_count` 0 + `behind` 1 +
unchanged `revision`) is documented in the CD+G and lyrics docs, up to 2% scroll drift during
ramps is recorded as accepted, and K7 is dropped.

Your three open requests are merged:

- Request 2, clock at a given time (K4, PR 196): ask what will be heard at your next frame's
  present time, not just "now". Past the device buffer the clock keeps running up to 250 ms
  instead of freezing. On macOS/iOS the time base matches display-link timestamps, so pass those
  straight in.
- Request 3, lyrics without a soundfont (K6, PR 195): open with `FA_OPEN_MIDI_LYRICS_ONLY`
  (.NET `StreamOptions.MidiLyricsOnly`). No soundfont, no synth; the stream reads silence and the
  lyric surface draws the same frame at whatever time you pass. Live MIDI or soundfont changes on
  it return not-implemented.
- Request 6, text shaping (K8, PR 197): Arabic, Hebrew, Indic and mixed-direction lyrics use each
  OS's own shaper (DirectWrite, Core Text, system HarfBuzz on Linux, the system shaper on Android
  12+). Nothing bundled. Android 7 to 11 keep left-to-right layout.

These are newer than rc.1, so please pin the newest CI build on the feed. Summary:
`fatzaudio/karaoke/2026-11-03_FatzAudio_Summary_KaraokeTiming.md`. All your 2026-11-01 requests
are now done or dropped.

Questions:

1. For lyrics-only files, is a silent stream you never play enough, or do you want a lyrics
   surface with no stream at all?
2. Which frame timer do you use on each platform (for K4's present-time input)?
3. Naming proposal from Keith. Put the family under one umbrella, FatzMedia, published by
   FatzBomb Entertainment:
   - `FatzMedia.Audio` (today's FatzAudio), `FatzMedia.Video`, and later `FatzMedia.Player`.
   - Each is its own package and native binary, versioned and shipped separately. An optional
     `FatzMedia` suite package can pull them together.
   - FatzBomb Entertainment goes in the package Authors/Company fields, copyright, LICENSE and
     assembly metadata, not in the names.
   - Audio-only apps reference `FatzMedia.Audio` directly and skip your engine, which takes that
     work off your hands.
   - CD+G and MIDI lyrics stay inside `FatzMedia.Audio`, since they run on the audio clock and
     come with the audio. A video layer that wants CD+G calls Audio's surface rather than owning
     a copy.

   Does this fit your layering? What name would your engine take (today FatzBomb.MediaControl /
   FatzBombMedia), and should "FatzMedia" alone mean the suite? We'd rename before 1.0.0.
