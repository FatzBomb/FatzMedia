# Idea Stage 033A - FFmpeg Audio Parity and Unsupported-Format Fallback

Status: Good idea. Owner decision (2026-10-17): next stage after the manual smoke of the
2026-10-16 stages. Two slices.

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
- Memory: 16-bit stereo 44.1 kHz PCM is about 10 MB per minute. Up to a configurable
  limit (default about 20 minutes), the WAV stays in memory; above it, it goes to a temp
  file that is deleted when the item closes.
- Prepared-next converts during `PrepareNextAsync`, so the transition is still instant.
- The analysis cache and silence trimming key on the original file's identity, not the
  converted buffer.
- Fallback opens report `MediaDiagnosticLevel.Info` ("decoded with FFmpeg") so hosts
  can see which path ran.

Rejected alternative: a live FFmpeg pull stream for audio-only files. It would need its
own seek, length, loop, trimming, and analysis code to reach parity, which is the
duplication this slice avoids. Slice 1 still uses live decode for video, where the audio
must stay locked to the picture.

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
