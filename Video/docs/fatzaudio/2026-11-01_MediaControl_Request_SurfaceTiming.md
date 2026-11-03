# FatzBomb.MediaControl: requests for the karaoke surfaces (timing and stalls)

To: FatzAudio. From: FatzBomb.MediaControl, 2026-11-01. Based on reading and measuring FatzAudio
`main` @ 17d2284 (PR 170) for our stage 037A; the evidence, with file:line references and
numbers, is in `2026-11-01_MediaControl_Analysis_KaraokeHandOff.md`.

**Status 2026-11-03:** FatzAudio 1.0.0-rc.1 answered requests 1, 4, 7, 8 and 9; 5 is dropped; 2, 3
and 6 are open. See `2026-11-03_MediaControl_Reply_Rc1.md`. Request 9 as built: RMS over 50 ms
windows at -50 dBFS by default (peak mode available), applied with the existing CUE_IN/CUE_OUT
markers rather than a new option.

Short version: the surfaces are in very good shape. Sync, seeking and tempo handling are as good
as ours or better, and the CD+G fixes, Shift-JIS and effects are real gains for us. Nine
requests, in priority order (8 was found by our slice-0 spike and blocks our karaoke slice;
9 was added 2026-11-02 and is not about the surfaces):

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
8. **Render-ahead must never starve audio decoding, and underruns should be visible.** Measured
   in our slice-0 spike (`updates/037A_0/StageUpdate.md`): two streams playing with 4K surfaces at
   maximum effects and render-ahead on, the stream clocks fell 170-580 ms behind wall time within
   30 s (audible dropouts); with render-ahead off they held within 14 ms. Please give decoding
   strict priority over render-ahead jobs (or cap render-ahead's share of the workers), and expose
   the per-stream underrun count you already keep, so hosts can detect it and back off.
9. **Silence detection (added 2026-11-02).** The owner decided silence detection belongs in the
   audio engine. Today MediaControl finds leading, trailing and internal silence itself and trims
   playback to the audible part (an optional "trim silence" setting), but only from the second
   play: our scan finishes about 0.5 s after the open, too late for the first. Please add silence
   regions to the stream (or to track analysis): where the audio is below a threshold (default
   about -60 dBFS) for at least a minimum length (default about 1 s), as a list of start/end
   times, plus the first and last audible time. Two things make it useful for trimming: it must
   be known before the first audible sample of a first play (a fast leading/trailing scan at open,
   or the result from your analysis cache), and a stream option to start at the first audible time
   and end at the last one, so the host need not seek. MIDI and tracker files can answer from
   their events. For video audio we push decoded samples, so a way to run the same detector on
   pushed or decode-only audio would let us use one rule everywhere.
