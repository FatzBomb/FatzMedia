# Stage 053A (planned) - Seekable stream media source

Owner decision 2026-11-05 (TriviaBomb pack files; bundled, encrypted or embedded media). Queue item 4.

- `MediaInputSource.FromStream(Func<Stream> open, string displayName, string? extensionHint)`: an
  opener, so playback, looping, seeking and audio each open their own reader; the session owns and
  disposes what it opens.
- Video (with sound, and silent looping backgrounds) through FFmpeg custom I/O (`AVIOContext`: read,
  seek, `AVSEEK_SIZE`), nothing written to disk. Callbacks re-enter the player's diagnostic owner.
- Audio-only: FatzAudio. Open question for FatzAudio first: its I/O-callback streams are not scanned
  for loudness ("cannot read it twice"); a re-openable I/O source would let it scan and cache them.
- Until then hosts copy entries to temp files once per run.
