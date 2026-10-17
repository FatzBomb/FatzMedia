# Stage Update

Stage: 033A.2 - FFmpeg fallback for unsupported audio formats
Date: 2026-10-17

## Status: IMPLEMENTED - manual Player smoke pending

Delivered (see the stage file's slice 2 criteria):

- `FileTypes.FfmpegAudio`, `FileTypes.IsPlayableAudio`, and `FileTypes.RegisterFfmpegAudio()`
  (public, called by the Windows and Linux bootstraps after FFmpeg loads). The list covers
  audio-only formats FFmpeg decodes, including AAC/M4A, ALAC, FLAC, Opus, AC-3, E-AC-3,
  DTS, WMA, APE, WavPack, TTA, TAK, Musepack, DSD (`.dsf`/`.dff`), MKA, CAF, and AMR.
- `FfmpegAudioFallback` and `ConvertedAudio` (new, `Core/Audio`): decode with the slice 1
  `FfmpegAudioReader`, write a 32-bit float WAV (`FloatWav` header), keep it in memory up to
  160 MB or spill to a temp file, and open it as a normal BASS stream.
- `AudioStreamManager.OpenFromPath` uses the fallback for unclaimed extensions, and as a
  retry when a BASS-claimed file fails to open. Prepared-next analysis converts once and
  hands the result to the transition.
- `AcceptedMediaTypes.Audio` (and so the Player's open-file filter) includes the fallback
  extensions.

Tests: 7 focused cases (`FfmpegAudioFallbackTests.cs`); the opt-in native test
`FfmpegAudioReaderNativeTests` gains a conversion round trip.

## Decisions made without the owner

- **Quick conversion to 32-bit float WAV** rather than a second live decoder (as planned).
  Float, not 16-bit, so lossy decoders that overshoot full scale do not clip; this doubles
  the memory estimate in the plan to about 21 MB per minute at 44.1 kHz stereo.
- In-memory limit 160 MB (about 8 minutes at 44.1 kHz stereo, 7 at 48 kHz); longer files go
  to a temp file under the existing `FatzBombMediaControl` temp folder, deleted on close.
  WAV caps a conversion at 4 GB (about 3 hours at 48 kHz stereo); longer files are refused
  with a warning.
- BASS stays first for every extension a loaded plugin claims. FFmpeg is the fallback,
  including when BASS claims the extension but cannot decode the file.
- Only the most recent prepared conversion is kept (one slot, shared by the process). It is
  freed when its item leaves the prepared-next slot (cleared, replaced, or stale) or its
  player is disposed.
- Files inside archives (MP3+G zips) still need a BASS plugin; the fallback covers plain
  files and `.cdg` companions only.
- Opening a fallback file decodes the whole file before playback starts. The time is
  logged; the manual smoke below measures it on real hardware.

## Verification

- Linux: Release build clean; `FfmpegAudioFallbackTests` 7/7.
- Real FFmpeg 9.0.2 on Linux: a generated 3 s 48 kHz stereo WAV converts to a float WAV
  of exactly the expected size, both in memory and as a temp file (forced with a 64 KB
  limit). FFmpeg reads both back with the right rate, channels, duration, and peak. The
  temp file is deleted on dispose.
- BASS's own documentation lists 32-bit WAV, float included, as natively supported.
  Opening the converted WAV in BASS is covered only by the manual smoke below.

## Manual smoke (Windows, native DLLs)

1. Move `bassflac.dll` out of `native\` and open a FLAC: it plays, and the Log panel shows
   "Decoded '...' with FFmpeg in N ms". Note N for a typical song. Seek, loop, tempo,
   pitch, EQ, fades, and Normalize all work. Put the DLL back afterwards.
2. Open a format no plugin covers, such as `.ape`, `.wv`, `.dsf`, or `.mka`: it plays the
   same way and appears in the open-file dialog's Audio filter.
3. An ALAC `.m4a` without `bassalac.dll`: BASS fails, FFmpeg takes over, and it plays.
4. A long file (over 9 minutes): the Log panel says "temp file". After closing it, the WAV is
   gone from `%TEMP%\FatzBombMediaControl`.
5. With `--trim-silence`, prepare a fallback file as the next item: the slot reaches Ready
   with analysis, and the transition starts without a second "Decoded ... with FFmpeg"
   line.
6. `song.ape` next to `song.cdg`: opening the `.cdg` pairs them and the lyrics stay in sync.
7. Regression: MP3, FLAC (with `bassflac.dll`), and WMA still open through BASS, with no
   "Decoded with FFmpeg" lines.

## Review follow-up (same day)

An independent review of slices 1 and 2 and stage 034A found no deadlock, handle misuse, or
WAV-header problem. Fixed here:

- The prepared conversion was only freed by a later prepare, so a temp WAV for a long file
  could stay on disk after clearing the slot or closing the app. It is now released as
  described above. Temp WAVs older than a day, which a crashed or killed run left behind,
  are swept on the first conversion of a run.
- If the disk filled while a conversion moved from memory to its temp file, the file
  handle leaked and the partial file stayed. The handle is now closed so the file is
  deleted.
- The in-memory buffer is sized from the file's duration instead of growing by doubling,
  which could hold up to twice the converted size.

Extra manual smoke: prepare a fallback file over 9 minutes, then Clear in the Next panel:
its WAV disappears from `%TEMP%\FatzBombMediaControl`.

