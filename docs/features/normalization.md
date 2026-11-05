# Loudness normalization

Normalization makes every stream play at the same perceived loudness, measured to **ITU-R BS.1770-4 / EBU R128** (the standard broadcasters and streaming services use). You turn it on once for the engine. From then on every stream gets the right gain automatically, with no analysis pass in your app and no waiting before playback.

## Quick start

C:
```c
fa_engine_set_normalization(engine, FA_NORMALIZE_TRACK, -16.0);  /* target in LUFS */

fa_stream* s;
fa_stream_open_path(engine, "concert.mkv", NULL, &s);  /* analysis starts here, in the background */
fa_stream_attach(s, device, NULL);
fa_stream_play(s);                                      /* plays at once, at the right level */
```

C#:
```csharp
engine.SetNormalization(NormalizeMode.Track, -16);

using var song = engine.OpenFile("concert.mkv");
song.PlayOn(output);
```

That's all. There is no rate or gain to set afterwards.

## Where the loudness comes from

For each stream, the first of these that is available wins:

| # | Source | Speed | `fa_loudness.source` |
|---|---|---|---|
| 1 | A value your app stored earlier, given with `fa_stream_set_loudness` | instant | `FA_LOUDNESS_APP` |
| 2 | ReplayGain 2 or R128 tags in the file (ID3v2 `TXXX`, FLAC and Ogg Vorbis comments, OpusTags, APEv2, WMA/ASF attributes) | instant | `FA_LOUDNESS_TAGS` |
| 3 | The scan cache (`fa_engine_config.cache_directory`), keyed by the file's content | instant | `FA_LOUDNESS_CACHE` |
| 4 | Only with `fa_engine_set_dialnorm` on: a Dolby (AC-3, E-AC-3) file's stated dialogue level ([dialnorm](dolby.md#dialogue-level-dialnorm)) | instant | `FA_LOUDNESS_DIALNORM` |
| 5 | A background scan of the file, then written to the cache | see below | `FA_LOUDNESS_SCAN` |
| 6 | While the scan runs: the loudness of the scan chunks finished so far, which lie all over the file (not final) | as chunks finish | `FA_LOUDNESS_PARTIAL` |
| 7 | Before any chunk is done, and for streams that cannot be scanned: a running estimate over the last 15 s of decoded audio (not final) | continuous | `FA_LOUDNESS_ESTIMATE` |

### Why the scan is fast, even on large video files

- **Only audio is decoded.** The scanner opens the file through the same decoder registry as playback, so a video container is read through its audio track alone and video packets are never decoded. MP4, MOV, MKV and WebM files are scanned this way: only the selected audio track's bytes are read.
- **All cores at once.** The file is cut into chunks at 100 ms boundaries and each chunk is scanned by its own decoder on its own thread, starting one second early so the filters are settled. The chunks' results are joined exactly, so the answer is identical to a single pass.
- **Once per song.** With a cache directory set, the result is stored under a fingerprint of the song's audio, so renamed, moved or retagged files are still recognised and nothing is scanned twice. See [the cache](#the-cache) below.
- **A good guess early.** Chunks are taken in a spread order (the start, the middle, the quarters, ...), and each chunk that finishes while others still run gives the stream the loudness of every chunk finished so far (`FA_LOUDNESS_PARTIAL`). That guess covers the whole song, not just its intro. The final result is joined in file order, so it is unchanged.
- **Nothing waits for it.** Analysis starts when the stream is opened, on scan threads separate from the playback workers (one per core, minus one left for playback).

- **True peak only where it can matter.** The 4x oversampling filter runs eight output samples at a time on SIMD (SSE on x86, NEON on ARM), sixteen with AVX on x86 processors that have it (picked at run time, so one binary still runs on older CPUs), and blocks of 256 frames that cannot beat the peak found so far are skipped (no interpolated point can exceed a known bound times the samples it comes from). Every path adds the same products in the same order, so the result is the same, to the bit, as computing every point one at a time.
- **Two channels per filter pass.** The K-weighting filter runs a pair of channels in one loop, so each channel's steps fill the other's wait on its previous sample. The arithmetic per channel is unchanged, so the measured loudness is identical.

Measured on the CI test file (3 minutes of stereo 44.1 kHz WAV, 4-core container): about 0.4 s, roughly 450x real time. A 10-minute 192 kbit/s MP3 scans in about 0.6 s on 4 cores (1000x real time), faster than decoding it once on one core.

A 4-minute 44.1 kHz stereo song, loudly mastered, on a 4-core 2.1 GHz x86 container (2026-11-03): about 0.1 s for FLAC, 0.12 s for 192 kbit/s MP3 and 0.18 s for AAC on all cores. On one core the loudness math (filter and true peak, decoding excluded) takes about 0.26 s of that, and decoding 0.13 s (FLAC) to 0.34 s (AAC).

### When playback starts

A first play waits briefly for the final value (200 ms by default, see below); otherwise playback never waits. It starts with the best value known at that moment. When the exact value arrives, the gain **glides** to it (at most 12 dB/s down, 6 dB/s up) so no jump is heard, and `FA_EVENT_LOUDNESS_FINAL` is raised. If the exact value is already known when the stream starts playing, which is the usual case for tagged, cached or short files, the first sample already has the exact gain.

When a stream is first heard on a value that is not final yet, `FA_EVENT_LOUDNESS_PROVISIONAL` is raised, with the `fa_loudness_source` it started on in `value` (`FA_LOUDNESS_PARTIAL`, `FA_LOUDNESS_ESTIMATE` or `FA_LOUDNESS_NONE`). Log it next to `FA_EVENT_LOUDNESS_FINAL` to see how often a guess was used.

#### A short wait for the first play

A song with a quiet intro could start on a guess and then glide audibly. To avoid that, a first play waits briefly for the final value: 200 ms by default. Change it, or cap the boost of a guess:

C:
```c
fa_engine_set_normalization_start_wait(engine, 200, 6.0);  /* wait up to 200 ms; a guess boosts at most +6 dB */
```

C#:
```csharp
engine.SetNormalizationStartWait(TimeSpan.FromMilliseconds(200), maxProvisionalBoostDb: 6);
```

- `fa_stream_play` of a stream that hasn't been heard since it was opened holds its first audio until the loudness is final or the wait has passed. The wait counts from the moment the stream would have started. 200 ms still feels instant, and on a current desktop most songs are measured by then (a 4-minute MP3 takes about 0.17 s on 4 cores). Tagged and cached songs start at once.
- If more than 75% of the audio is scanned when the wait runs out, the scan is nearly done (at that pace the rest takes about a third of the time so far), so it keeps waiting for the final value, for at most the wait again. That avoids starting on a guess and getting the final value a moment later.
- Otherwise the stream starts on the best value so far (`FA_LOUDNESS_PARTIAL`, else the estimate), raises `FA_EVENT_LOUDNESS_PROVISIONAL`, and glides to the final gain as usual.
- While held, the state is `FA_STATE_PLAYING` but nothing is mixed. The position and `fa_stream_get_clock` stay at the start, so CD+G and video wait with the audio.
- These never wait: scheduled starts (`fa_stream_play_at`, crossfades, `fa_stream_set_next`, sync and auto-mix), streams faded or crossfaded away while held, streams on a DJ mixer deck, streams opened with `FA_OPEN_NO_NORMALIZE`, and streams with no scan to wait for (live, web, disc and app-I/O streams).
- The second value caps the boost while the loudness is not final, with or without a wait: `0` allows only cuts until the value is final, `6` keeps a guess from boosting a quiet intro by the full 12 dB. The default is 12, which adds no cap.
- Defaults: a 200 ms wait and no extra cap. `0` turns the wait off (playback then starts at once, as in 1.0.0-rc.3 and before). A new wait applies from the next start.

### Live streams and app I/O

Streams that can't be read a second time (`fa_stream_open_io` and [web streams](web-streams.md)) are never scanned. They use the running estimate, which adapts as the program changes. You can still give them a value with `fa_stream_set_loudness`.

## The cache

Set `fa_engine_config.cache_directory` (.NET `AudioEngineOptions.CacheDirectory`) and each song is measured once, ever. Songs with ReplayGain or R128 tags never use the cache: tags are read first.

- **One file.** `loudness.fac` holds 48 bytes per song. It is read into memory once, on the first lookup, on a scan thread (never the audio thread): a full 5 MB file (109,000 songs) takes about 16 ms, and each lookup after that takes microseconds. New results are appended.
- **Size limit.** 5 MB by default; change it with `fa_engine_set_cache_size(engine, bytes)` (.NET `SetCacheSize`, or `AudioEngineOptions.CacheMaxBytes`), at least 16 KB. When the file outgrows the limit it is rewritten, keeping the songs **used** most recently within 3/4 of it. Used means found by a lookup or measured by a scan, not when the entry was written, so a song played often is never the one dropped. A hit updates the time in memory at once, and in the file at most once a day per song (and in full at every rewrite).
- **Clear.** `fa_engine_clear_cache` (.NET `ClearCache()`) deletes the cached loudness and the cached [track analysis](analysis.md) (FatzAudio's own files in the folder only). Songs are measured again as they are played.
- **Shared and crash-safe.** Each result is appended in one write that another app's appends can't split, so several apps (or engines) may share the folder. A record cut short by a crash fails its checksum and is skipped; the file is then rewritten clean. Rewrites go to a temporary file that replaces the old one by rename, so a crash leaves one whole file or the other. If two apps rewrite at the same moment, a few new results may be lost and those songs are measured again; the file is never corrupted.
- **The fingerprint** covers the audio data only, not the file's name or its tags:

| Format | What is fingerprinted |
|---|---|
| WAV, RF64, AIFF | The whole `data` / `SSND` chunk (up to 1 GB) and the format chunk, so an edit in place that keeps the length is noticed |
| MP4, M4A, MOV | The `mdat` box (the tags are in `moov`), at 16 points |
| FLAC | The frames after the metadata blocks (comments, pictures, padding), at 16 points |
| Ogg Vorbis, Opus, FLAC | From the first audio page (the comment header is before it), at 16 points |
| MP3, AAC and the rest | The file without ID3v2 at the start and ID3v1, APEv2 or Lyrics3 at the end, at 16 points |

"16 points" means 4 KB read at 16 places spread evenly over the audio, plus its length: a coded stream changes throughout when it is encoded again, so this is enough and stays fast for multi-GB video files. A MIDI file's key also includes the soundfont and rate it plays with. Results cached by 1.0.0-rc.3 or earlier (one small `.loudness` file per song) are not read; `fa_engine_clear_cache` deletes them.

## Gain

`gain = target − loudness`, limited to **−30 … +12 dB**. Silent or unmeasurable streams get 0 dB. `fa_loudness.gain_db` tells you the gain a stream gets with the current settings. Dolby 5.1 and 7.1 mixed down to stereo get back what their downmix took off to stay below full scale, on top of that ([stereo downmix](dolby.md#stereo-downmix)).

| Target | Typical use |
|---|---|
| −14 LUFS | Matches Spotify, YouTube and similar services |
| −16 LUFS | Music players, karaoke, podcasts (a good default) |
| −23 LUFS | EBU R128 broadcast |

Changing the target or mode while streams play takes effect at once, with the same glide.

### Track and album mode

| Mode | Behaviour |
|---|---|
| `FA_NORMALIZE_OFF` | Default. No gain. Analysis still runs when you ask for a stream's loudness. |
| `FA_NORMALIZE_TRACK` | Every stream to the target. |
| `FA_NORMALIZE_ALBUM` | Album gain from tags when present, so the quiet and loud songs of an album keep their balance; otherwise per track. A value from `fa_stream_set_loudness` wins in both modes. |

### Opting a stream out

Open it with `FA_OPEN_NO_NORMALIZE` (.NET: `new StreamOptions { NoNormalize = true }`) for jingles, sound effects or mixes that are already mastered. Decode-only streams (`FA_OPEN_DECODE_ONLY`) are never normalized: `fa_stream_read` returns the source's own samples.

## True-peak limiter

A boost can push peaks over full scale, and so can two loud streams crossfading. Every output device therefore has a **true-peak limiter**: it detects peaks between samples too (4x oversampling), looks 1.5 ms ahead so it is already at the right gain when a peak arrives, releases smoothly over about 150 ms back to exactly unity gain, and never touches audio below the ceiling.

```c
fa_engine_set_limiter(engine, FA_LIMITER_AUTO, -1.0);  /* the default: on while normalization is on */
```

| Mode | Behaviour |
|---|---|
| `FA_LIMITER_AUTO` | On while normalization is on (default) |
| `FA_LIMITER_ON` | Always on |
| `FA_LIMITER_OFF` | Always off |

The look-ahead (about 1.9 ms in total) is always in the signal path, also while the limiter is off, and the limiter keeps measuring while off. Switching it on or off fades over 20 ms and a new ceiling applies to audio not yet in the look-ahead, so neither ever clicks or clips. `fa_device_get_latency` and `fa_stream_get_clock` include it, so video and CD+G stay in sync.

## Reading and storing loudness

C:
```c
fa_loudness l = { .struct_size = sizeof(l) };
fa_stream_get_loudness(s, &l);        /* never blocks; starts the analysis if needed */
if (l.is_final)
    save_to_library(path, l.integrated_lufs, l.range_lu, l.true_peak_dbtp);

/* Next time, skip all analysis: */
fa_loudness known = { stored_lufs, NAN, NAN };
fa_stream_set_loudness(s, &known);

/* Or measure files without playing them (e.g. a library import), blocking: */
fa_scan_loudness_path(engine, "song.flac", &l);
```

C#:
```csharp
song.LoudnessFinal += (_, _) =>
{
    var l = song.GetLoudness();
    library.Save(path, l.IntegratedLufs, l.RangeLu, l.TruePeakDbtp);
};

song.SetLoudness(storedLufs);                                  // next time

var scanned = await Task.Run(() => engine.ScanLoudness("song.flac"));
Console.WriteLine($"{scanned.IntegratedLufs:F1} LUFS, LRA {scanned.RangeLu:F1} LU, {scanned.TruePeakDbtp:F1} dBTP");
```

| Field | Meaning |
|---|---|
| `integrated_lufs` | Integrated loudness (gated, BS.1770-4). `-INFINITY` for digital silence. |
| `range_lu` | Loudness range, EBU Tech 3342. |
| `true_peak_dbtp` | True peak. From tags this is the tagged sample peak. |
| `is_final` | 1 when the source is tags, cache, scan, app or dialnorm; 0 for a partial scan, an estimate or nothing yet. |
| `source` | `fa_loudness_source`, see the table above. |
| `gain_db` | Gain normalization applies to this stream right now. |

Values that aren't known are `NAN` (.NET: `double.NaN`), for example the range and peak of an estimate.

## Threads

- Scans run on their own scan threads, never on the decode workers or the audio thread. `fa_scan_loudness_path` also uses the calling thread.
- The running estimate is computed by the decode worker that fills the stream, on audio it decodes anyway, and only once something wants it: normalization on, or the app asking with `fa_stream_get_loudness`. With normalization off (the default) and no one asking, it costs nothing; the first `fa_stream_get_loudness` starts it, so its estimate covers the audio decoded from then on.
- The audio thread only reads the gain (atomics) and moves it a little per block.
- The partial value is computed by the scan thread that finished a chunk; the audio thread only reads it.
- `FA_EVENT_LOUDNESS_FINAL` and `FA_EVENT_LOUDNESS_PROVISIONAL` are delivered on the event thread like every other event.
- Closing a stream stops its scan at once; it never waits for a scan to finish.

## Accuracy

The meter is tested against the EBU compliance cases (Tech 3341 integrated loudness with gating at 44.1 and 48 kHz, Tech 3342 loudness range) to ±0.1 LU, true peak to the BS.1770 tolerance (flat to 20 kHz at 48 kHz, never reading high, sample peaks and the last samples of a file included), channel weights for quad, 5.1 and 6.1 (surrounds +1.5 dB, LFE excluded), and a chunked scan against a single pass to 0.01 LU. See `tests/test_loudness.c`.

## Pitfalls

- **Don't set the volume to normalize.** `FA_PARAM_VOLUME` stays yours for fades and user volume; normalization is applied on top of it.
- **ReplayGain 1 tags** (89 dB reference) are read as ReplayGain 2 (−18 LUFS reference); most taggers have written RG2 for years. If your library has old tags, set `fa_stream_set_loudness` from your own data instead.
- The cache only knows what is in its directory. Point every engine of your app at the same directory to share results.
