# Completed Stage 033A - FFmpeg Audio Parity and Unsupported-Format Fallback

Prepared: 2026-10-17
Status: Completed 2026-11-06. Slice 1 (equalizer, fades, normalization for video audio): owner
smoke passed 2026-10-17. Slice 2 (FFmpeg fallback for unsupported audio formats): owner smoke
passed 2026-11-06 with real 200+ MB `.ape` and `.dsf` files (WinForms player, BASS): both played
and seeked; the whole-file conversion took 6.0 s for the APE (698 MB temp WAV) and 1.1 s for the
DSF (806 MB at 352.8 kHz) before playback started. Not optimised: this is the BASS path, which
goes away with 037A, and FatzAudio is asked to decode APE and DSD itself (request 15). The other
fallback formats ran in the Linux format matrix (`updates/033A_2`). Owner decision (2026-10-17): implement now while the
owner's native smoke of the 2026-10-16 stages is pending.

## Why This Is Worth Doing

Audio decoded by FFmpeg (the sound of every video file) misses features that plain
audio files get through BASS:

| Feature | Plain audio (`AudioEngine`) | Video audio (`VideoAudioEngine`) |
|---|---|---|
| Equalizer (020A) | yes | no: excluded from the 020A slice |
| Fades and faded transitions (031B) | yes | no: fades return `false`, transitions are unfaded |
| Normalization | yes (peak analysis) | no: gain fixed at 1.0, so the Normalize toggle does nothing |
| Silence trimming (030B) | yes | no, by design (see Non-Goals) |

Separately, an audio file whose extension no BASS plugin claims does not open at all,
even though FFmpeg can decode nearly every audio format. Hosts keep the BASS plugins
they want; anything else falls back to FFmpeg without losing features.

## Recommended Direction

### Slice 1: EQ, fades, and normalization for video audio

`VideoAudioEngine` already decodes with FFmpeg, converts to PCM with `swr_convert`,
pushes it into a BASS push stream, and plays it through a BASS_FX tempo stream
(`PlayHandle`). The equalizer (BASS_FX PEAKEQ) and the fade gain stage (BASS_FX_VOLUME)
are effects on a BASS channel, so they can attach to that same `PlayHandle`.

- Extract the equalizer and fade-gain handling from `AudioEngine` into helpers that
  work on any BASS play handle, then have `VideoAudioEngine` implement
  `IAudioFilterHost` and `IAudioFadeGainTarget` through them. Same chain order as plain
  audio: equalizer, then normalization and volume, then fades.
- Normalization: a background FFmpeg decode pass computes the audio peak, using the
  same analysis cache keyed by file identity. Long videos need the quick-peak path
  first (a bounded window, like `AnalyzePeakQuickAsync`), refined by the full scan;
  `IsNormalizationReady` stays false until a value exists, and gain changes ramp.
- Faded prepared-next transitions into and out of video fade the audio only. The
  picture is untouched (video dissolve remains a 031B non-goal).

### Slice 2: FFmpeg fallback for unsupported audio formats

Chosen approach: **quick conversion, not a second live audio path.** When no BASS
plugin claims an audio extension and FFmpeg can open the file, decode its audio with
FFmpeg to PCM WAV and open the result through `AudioEngine`'s existing in-memory stream
path (the one archive entries use). The file then gets every plain-audio feature
(seek, loop, tempo, pitch, equalizer, fades, normalization, trimming, prepared-next)
with no new playback code.

- Decode speed is typically far faster than real time for audio-only files, so a song
  converts in well under a second on a modern PC. The stage measures this before
  committing to the threshold below.
- Memory (as built: 32-bit float, so about 21 MB per minute at 44.1 kHz stereo): up to
  160 MB (about 8 minutes) the WAV stays in memory; above it, it goes to a temp file that
  is deleted when the item closes.
- Prepared-next converts during `PrepareNextAsync`, so the transition is still instant.
- The analysis cache and silence trimming key on the original file's identity, not the
  converted buffer.
- Fallback opens report `MediaDiagnosticLevel.Info` ("decoded with FFmpeg") so hosts
  can see which path ran.

Rejected alternative: a live FFmpeg pull stream for audio-only files. It would need its
own seek, length, loop, trimming, and analysis code to reach parity, which is the
duplication this slice avoids. Slice 1 still uses live decode for video, where the audio
must stay locked to the picture.

## Slice 2 Acceptance Criteria

- `FileTypes.FfmpegAudio` (registered by the Windows and Linux bootstraps once FFmpeg
  loads) lists audio-only extensions FFmpeg decodes. A plain file whose extension only
  that set claims routes to `FileType.Audio`, pairs with a `.cdg` companion, and appears
  in `AcceptedMediaTypes.Audio`.
- `AudioStreamManager.OpenFromPath` converts such a file with `FfmpegAudioFallback` and
  opens the result as a normal BASS stream (in memory, or a temp file above 160 MB that
  is deleted on close). A BASS-claimed file BASS cannot decode (for example ALAC in
  `.m4a` without `bassalac`) retries through the same conversion.
- The converted format is 32-bit float WAV at the source rate, at most two channels (the
  format the video audio engine already plays), so nothing clips and BASS reads it
  natively.
- Prepared-next analysis of such a file converts it once and keeps the result for the
  transition. The analysis and normalization caches key on the original file.
- Diagnostics report each conversion (time, size, memory or temp file).
- Focused tests for routing and the WAV header; the opt-in native test round-trips a
  conversion through FFmpeg in memory and through a temp file.

## Slice 1 Acceptance Criteria

- `VideoAudioEngine` implements `IAudioFilterHost` and `IAudioFadeGainTarget`; the
  equalizer (`BassEqualizerFilter`) and the fade stage (`BassFadeGainStage`, extracted from
  `AudioEngine` unchanged) attach to its tempo stream, in the same chain order as plain
  audio: equalizer, normalization gain FX, fade FX, channel volume attribute.
- Normalization is a gain FX (it can amplify), driven by a background `FfmpegAudioReader`
  that owns its own demuxer. `FfmpegAudioPeakScanner` samples the same windows as the BASS
  analysis (quick: first 6 s; deep: up to 240 half-second windows across the file), and
  the gain uses the same `AudioNormalizer.CalculateGainFromPeak`. Deep results are cached
  per file identity (path, size, write time) or archive entry.
- `IsNormalizationReady` is false until the quick pass finishes, so play-on-load waits for
  it exactly as for plain audio. Gain events are raised in order on the thread pool, never
  on the analysis thread, which `Close` joins.
- Stop, a natural end, and a gapless loop wrap restore full fade gain (as plain audio does).
- Focused tests for the pure parts; an opt-in native test checks the reader and scanner
  against real FFmpeg on Linux.

## Recommended Immediate Action

Measure FFmpeg decode-to-PCM time and peak memory for representative files (FLAC, APE,
WavPack, DSD, Opus, long MKA) on the owner's Windows machine, then fix the in-memory
limit. For slice 1, confirm PEAKEQ and BASS_FX_VOLUME on the tempo stream add no
latency that would move A/V sync.

## Must Preserve

- A/V sync and the existing video audio clock.
- Plain-audio behavior and performance; BASS stays the first choice for every format a
  loaded plugin claims.
- Bypassed-equalizer output identical to no equalizer.
- Core boundary: FFmpeg and BASS access stay in Core as today; nothing moves to
  adapters.

## Explicit Non-Goals

- Silence trimming for video or CDG-paired audio. Cutting audio time without moving the
  picture or graphics breaks sync (030B scope decision).
- Replacing BASS plugins the host ships. The fallback only covers unclaimed formats.
- Network video (deferred in 014A) and mid-playback output-device switching.
- Transcoding or exporting files for the user.
