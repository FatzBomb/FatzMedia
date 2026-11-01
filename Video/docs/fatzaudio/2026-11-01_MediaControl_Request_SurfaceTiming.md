# FatzBomb.MediaControl: requests for the karaoke surfaces (timing and stalls)

To: FatzAudio. From: FatzBomb.MediaControl, 2026-11-01. Based on reading and measuring FatzAudio
`main` @ 17d2284 (PR 170) for our stage 037A; the evidence, with file:line references and
numbers, is in `2026-11-01_MediaControl_Analysis_KaraokeHandOff.md`.

Short version: the surfaces are in very good shape. Sync, seeking and tempo handling are as good
as ours or better, and the CD+G fixes, Shift-JIS and effects are real gains for us. Seven
requests, in priority order:

1. **`update_into` never blocks the caller.** Today a first frame, layout change, seek, loop wrap
   or late frame redraws in full on the caller (`cdg.c:2595-2610`), and the call can wait on
   `work_lock` while a worker renders (`cdg.c:3247-3252`, `3216-3233`, `3108-3112`). At 4K with
   effects we measured 50-210 ms on such frames. Please return the last finished frame flagged
   `behind` and do full redraws on a worker; we keep showing the previous frame for those few
   frames. (Our original request said "never makes our render thread wait"; we will also call
   from our own karaoke thread, but a stall there still delays the next karaoke frame.)
2. **A clock at a given time:** `fa_stream_get_clock_at(stream, monotonic_ns, &out)` (the internal
   `fa_stream_clock_at`, `stream.c:3078`) plus the monotonic time base it uses, so we can draw for
   the moment a frame reaches the screen, the same moment the video frame uses.
3. **Lyrics without a soundfont:** a lyric surface (or a decode-only MIDI stream) opened from the
   file's bytes without needing instruments, so we can draw lyrics while another engine plays
   the audio and on machines with no GM bank (`FA_ERROR_NO_SOUNDFONT` today).
4. **Render-ahead under continuous tempo changes:** slots are keyed to tempo in 0.25% steps
   (`cdg.c:2970-2993`), so a DJ-sync ramp defeats render-ahead. Keying by source time, or
   re-timing slots, would keep it working.
5. **Sharp indexed output at a scale:** optional INDEXED8 output at an integer scale (with the
   palette and a palette revision), so palette animations (colour cycling, fades) stay a GPU
   lookup instead of multi-MB CPU redraws in scaled mode.
6. **Text shaping:** GSUB (ligatures) and bidi for Arabic, Hebrew and Indic lyrics, or a
   documented fallback (which font and what the user sees).
7. **Document the timing rules:** that CD+G packs are counted with ceil at a time
   (`cdg.c:2279-2286`; we use floor), and the offset sign (time - offset, `cdg.c:2345-2351`; ours
   is the opposite). A floor option would let our golden tests compare frame for frame.
