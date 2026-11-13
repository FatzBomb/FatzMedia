# Stage Update

Item: audit 2026-11-12, P2 - FFmpeg audio fallback memory
Date: 2026-11-12

## Status: implemented, pending manual smoke

Delivered (smallest safe step):
- A source FFmpeg decodes as 16-bit integers (`AV_SAMPLE_FMT_S16` / `S16P`) with at most two channels is now written
  as a 16-bit PCM WAV instead of a 32-bit float WAV: the same samples, half the size. Float sources (most lossy
  codecs) and anything that needs a downmix stay float, as before. `FfmpegAudioReader.Open(path, allowInt16Pcm: true)`,
  `IsInt16`, `BytesPerSample` and `ReadFrameBytes()` carry it; `ReadFrame()` (float) is unchanged for the video
  audio path and throws on an integer reader. `FfmpegAudioDecoderSetup` has the matching `TryOpen` overload.
- `Pcm16Wav` writes the 44-byte header; `PlanOutput` takes the bytes per sample.
- The in-memory limit goes from 160 MB to 64 MB: with up to four prepared conversions kept, one 4-minute stereo
  48 kHz float song (about 92 MB) now goes to a temp file, and the same song as integers (46 MB) stays in memory.
  The temp-file path is the existing one (deleted on dispose).

Not done, on purpose: streaming through a FatzAudio push or packet stream. Seek, loop, tempo, silence trim, the
normalization scan and prepared-next all need random access to the whole decoded file, which a push stream does not
give, so the converted-WAV design stays. A later step could convert in a background thread into the temp file while
playback starts from a growing file; that needs FatzAudio support for a still-growing WAV.

Checked here with the real FFmpeg DLLs (scratch harness outside the repo): a 16-bit WAV gives 16-bit output with the
exact samples and a 44-byte PCM header; a float WAV and a 6-channel 16-bit WAV stay float; sizes are as planned.

Manual smoke:
1. Play a file only FFmpeg decodes (for example a `.dts` or `.ac3`): it plays, seeks, loops and changes tempo as before.
2. Play a 16-bit PCM source that goes through the fallback (a 16-bit file with an extension FatzAudio does not claim):
   same sound as before; the log line "Decoded ... (N MB, memory)" shows about half the previous size.
