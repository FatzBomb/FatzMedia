# Stage 054A (planned) - Full mode idle-cost check

Owner decision 2026-11-05: no `Simple` player mode; fix idle costs in `Full` itself. Queue item 5.

- FatzAudio measured a neutral stream at about 0.3-0.5% of one core and 0.4 MB; tempo, EQ and CD+G
  cost nothing until used (its reply of 2026-11-05), so there is nothing to save on the audio side.
- Steps: the owner compares `--mode=full` and `--mode=background` on a plain 1080p clip (CPU, memory,
  fps, dropped frames); a read-only look at the per-frame presentation path for work done for unused
  features (empty CD+G, lyric or subtitle layers); fixes in `Full`. Reconsider a mode only with numbers
  showing a cost that cannot be fixed there.
- **Owner measurement 2026-11-06** (WPF, Direct3D11 native window, plain 1080p clip with sound, RTX 5090,
  20 cores, 8 stats lines each): Full 0.13 cores on average (0.08-0.24), working set about 300 MB;
  Background (no audio) 0.11 cores (0.05-0.19), about 291 MB; both about 30 fps, no drops. Full costs about
  0.02 of a core and 10 MB more, within the measurement's noise, while also playing the sound: nothing to
  fix on this desktop. Left open: the same check on a laptop (the shows' usual machine) before closing the
  stage; the read-only look at the per-frame path can wait for that.
- For slice 5: FatzAudio suggests `BufferMs` 250 for video soundtracks (half the decode-ahead memory).
