# FatzBomb.MediaControl: request 12, start a first play at a settled loudness

To: FatzAudio. From: FatzBomb.MediaControl, 2026-11-04. Measured on Linux against FatzAudio
1.0.0-rc.1 (59741a5) and `main` @ f5eeb1a (PR 201, the faster scan), 4-core 2.1 GHz x86 container,
null output, `NormalizeMode.Track` at -14 LUFS.

Our FatzAudio engine now uses your loudness normalization and limiter (037A); our own peak rule is
gone there. The one thing left is the first play of a song whose loudness is not known yet. We think
it belongs in FatzAudio, not in our player, because only the scan knows how far it has got.

## What we measured

Open to `FA_EVENT_LOUDNESS_FINAL`, median of 5 (4 cores unless stated):

| File | rc.1 | rc.1, 2 cores | main (PR 201) |
|---|---|---|---|
| 4 min MP3 192k | 208 ms | 387 | 174 |
| 4 min FLAC | 169 | 335 | 140 |
| 4 min AAC | 245 | 389 | 233 |
| 4 min Ogg Vorbis | 261 | 373 | 219 |
| 4 min Opus | 343 | 462 | 321 |
| 4 min WAV | 146 | 201 | 106 |
| 10 min MP3 | 520 | 712 | 435 |
| 60 min MP3 | 2942 | - | 2486 |
| ReplayGain tags / scan cache | 0-2 | - | - |

- Right after `fa_stream_open_*` the source is `FA_LOUDNESS_NONE` (gain 0 dB) even for a tagged
  or cached file; FINAL follows within about a millisecond.
- Played at once, the running estimate is there within 3-14 ms and is used from the first sample.
  A quiet file got the full +12 dB on the estimate; the glide (12 dB/s down, 6 dB/s up) then
  corrects it when the measurement differs, which is fine but audible on a song with a quiet intro.

## The request

An optional, engine-wide start wait for normalized streams:

1. **Hold the first audio until the loudness is final or a wait has passed**, for example
   `fa_engine_set_normalization_start_wait(engine, wait_ms)` (.NET
   `AudioEngine.SetNormalizationStartWait(TimeSpan)`), default 0 (today's behaviour). We would set
   about 250 ms: a first play then looks instant and, on a current desktop, is usually already
   measured; cached and tagged songs start at once.
2. **When the wait runs out, start at the best provisional value.** The scan already measures chunks
   spread over the whole file in parallel; a gated result over the chunks finished so far is a far
   better guess than the running estimate of the first decoded seconds (the intro). Then glide to the
   final value as today. A new `fa_loudness_source` value (say `FA_LOUDNESS_PARTIAL`) would tell us
   which one it started on.
3. **Events:** an event when playback starts on a provisional value (with its source), and
   `FA_EVENT_LOUDNESS_FINAL` as today, so we can log both.
4. **Clock:** the held time is not heard, so `fa_stream_get_clock` should stay at the start until
   the audio starts (CD+G and video follow it).
5. **Optional:** until the value is final, allow only a cut, or cap a provisional boost (for example
   at +6 dB), so a guess never boosts a quiet intro by the full +12 dB.

## Lower priority: the cache fingerprint

The scan cache key (size plus the first, middle and last 64 KB) handles a file replaced under the
same name correctly (tested). It can be fooled by an uncompressed WAV/AIFF edited in place with the
same length and those three blocks untouched: we turned most of a WAV down by 6 dB (-19.6 LUFS by
FFmpeg's `ebur128`) and the cache returned the old -14.9. Adding the file's modification time to the
key, or hashing all of an uncompressed file, would close it. We accept the risk for now.

## Our side meanwhile

Our engine starts a play-on-load at once (no wait of our own) and lets FatzAudio's estimate and
glide do the rest. When the start wait exists, we set it from our `NormalizationOptions` and change
nothing else.
