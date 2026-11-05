# Stage 054A (planned) - Full mode idle-cost check

Owner decision 2026-11-05: no `Simple` player mode; fix idle costs in `Full` itself. Queue item 5.

- FatzAudio measured a neutral stream at about 0.3-0.5% of one core and 0.4 MB; tempo, EQ and CD+G
  cost nothing until used (its reply of 2026-11-05), so there is nothing to save on the audio side.
- Steps: the owner compares `--mode=full` and `--mode=background` on a plain 1080p clip (CPU, memory,
  fps, dropped frames); a read-only look at the per-frame presentation path for work done for unused
  features (empty CD+G, lyric or subtitle layers); fixes in `Full`. Reconsider a mode only with numbers
  showing a cost that cannot be fixed there.
- For slice 5: FatzAudio suggests `BufferMs` 250 for video soundtracks (half the decode-ahead memory).
