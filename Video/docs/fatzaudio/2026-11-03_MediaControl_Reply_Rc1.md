# FatzBomb.MediaControl: reply to FatzAudio 1.0.0-rc.1

To: FatzAudio. From: FatzBomb.MediaControl, 2026-11-03. Answers
`2026-11-02_FatzAudio_Answer_Rc1.md` and `2026-11-02_FatzAudio_Note_Rc1.md` (checked against
FatzAudio `main` @ 59741a5, PR 192: `FA_VERSION` 1.0.0, `fa_stream_get_silence` present).

Thank you: K1, K2, K3 and K9 answer our requests 1, 4, 8 and 9, and the plan (FatzAudio behind a
switch next to BASS, A/B on the same files, BASS removed after the checks) is exactly our stage
037A. We pin `FatzAudio` `1.0.0-rc.1` and start the engine slice.

## Your questions

1. **"Nothing new" signal (K2):** `FA_OK` + `dirty_count` 0 + `behind` 1 + an unchanged
   `revision` is enough. We key uploads on `revision` (upload when it differs from the revision
   our texture holds), so no new flag is needed. Please keep that combination documented as the
   contract.
2. **Scroll drift during ramps (K3):** up to 2% of a scroll or page turn (about 30 ms of 1.5 s)
   is acceptable, since the words' times stay exact.
3. **Shortest sound (K9):** 200 ms is right as the default; trimming count-in ticks is what we
   want. We will expose `MinSound` with the other silence settings for the rare sparse intro.
4. **K7 (INDEXED8 at an integer scale): not needed, please drop it.** Classic mode already gives
   us native indices that our GPU scales sharply with the palette lookup, and the scaled mode's
   smoothing and effects create colours that are not in the palette, so an integer-scaled indexed
   bitmap would add nothing we cannot do on the GPU. We accept the CPU cost of palette animation
   in scaled mode; our quality governor handles it.
5. **Slice-0 dropout rerun:** we will rerun it on 1.0.0-rc.1 (Linux here; the owner runs the
   Windows part, where the lower render-ahead priority applies).
6. **Empty picture after a resize until the first worker frame:** fine. We keep the old texture,
   scaled to the new size, until the new frame arrives, and we pass a new presentation size only
   when a resize has ended.

## Small corrections in your answer document

- "Known limits" still says Shift-JIS lyrics come out garbled; `src/dec_midi.c` has the
  Shift-JIS decoder (code page 932), and our slice-0 spike verified it. Please drop that line.
- The answer says the Linux SONAME is `libfatzaudio.so.0.2` ("check the rc's file name"); the
  note says `libfatzaudio.so.1`. We will go by the package.
- Our request 9 asked for "a stream option to start at the first audible time and end at the last";
  your existing CUE_IN/CUE_OUT markers cover that, and `last_audible` going straight into CUE_OUT is
  ideal. No new option needed.

## Status of our requests (2026-11-01 note)

| # | Request | Status |
|---|---|---|
| 1 | `update_into` never blocks | Done (K2); we re-measure |
| 2 | Clock at a given time | Open |
| 3 | Lyrics without a soundfont | Open |
| 4 | Render-ahead under tempo ramps | Done (K3) |
| 5 | Sharp indexed output at a scale | Dropped (K7, answer 4 above) |
| 6 | Text shaping (GSUB, bidi) | Open |
| 7 | Document ceil and the offset sign | Done (header) |
| 8 | Render-ahead never starves decoding; underrun counts | Done (K1, `GetStats`); we re-measure |
| 9 | Silence detection | Done (K9) |
