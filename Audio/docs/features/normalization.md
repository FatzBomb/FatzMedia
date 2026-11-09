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

### Streams you feed: packets and decoded frames

A [packet or push stream](ffmpeg.md) (video audio that your app demuxes, a codec you decode yourself) has no file for FatzMedia.Audio to read a second time, so it is not scanned by itself and uses the running estimate. Two additions give it the same treatment as a file:

1. **Feed a scan.** Read the track once more in the background, faster than real time, and push it into a `fa_scan_feed`. FatzMedia.Audio measures it with the same meter as a scan by path and stores the result in the [cache](#the-cache).
2. **Name the song on the stream.** Open the playing stream with `fa_open_options.source_identity` (.NET `StreamOptions.SourceIdentity`). Normalization then looks the song up in the cache, and takes part in a feed of the same song while it runs: the [start wait](#when-playback-starts) and the provisional value work as for a file.

The identity of a song is the `fa_fingerprint` of the **original file** (`fa_fingerprint_path`, .NET `AudioFingerprint.Compute`) plus the audio track in it (`fa_open_options.audio_track` on the stream, `fa_scan_options.audio_track` on the feed; `-1` = the file's default track). Use `-1` when FatzMedia.Audio could also open the same file itself: a scan by path and a fed scan then share one cache entry.

C:
```c
fa_fingerprint id = { .struct_size = sizeof(id) };
fa_fingerprint_path(path, &id);                       /* the original file */

/* In the background, before the song is played (library import, or the next item of a queue): */
fa_scan_options so; fa_scan_options_init(&so);
so.identity = &id;
so.duration_seconds = duration;                       /* optional: gives the start wait a progress */
fa_scan_feed* feed;
fa_scan_open_packets(engine, &params, &so, &feed);    /* or fa_scan_open_push(engine, &format, &so, &feed) */
while (fa_scan_wants_more(feed) && read_next_audio_packet(&pkt))
    fa_scan_push_packet(feed, &pkt);                  /* or fa_scan_push_frame */
fa_loudness l = { .struct_size = sizeof(l) };
fa_scan_finish(feed, &l);                             /* stored in the cache; playing streams get it */
fa_scan_close(feed);

/* The playing stream names its song: */
fa_open_options o; fa_open_options_init(&o);
o.source_identity = &id;
fa_stream_open_packets(engine, &params, &o, &stream);
```

C#:
```csharp
var id = AudioFingerprint.Compute(path);
using (var scan = engine.OpenLoudnessScan(parameters, id, new LoudnessScanOptions { Duration = duration }))
{
    while (scan.WantsMore && ReadNextAudioPacket(out var packet))
        scan.PushPacket(packet);
    var loudness = scan.Finish();
}
using var audio = engine.OpenPackets(parameters, new StreamOptions { SourceIdentity = id });
```

- **Where it runs.** On the thread that feeds it: each push decodes and measures at once (far faster than real time: 180 s of PCM measure in 0.15 s on one core, plus the codec's decode time). FatzMedia.Audio starts no thread and buffers nothing for it. Feed the track from its start, in order, once; different feeds may run on different threads. Calls on one feed must not overlap.
- **Skip what is known.** `fa_scan_wants_more` is 0 when the result is known before any audio: the song is in the cache, or (`fa_engine_set_dialnorm` on) the first unit of a Dolby stream gave its dialogue level. Check it after opening and now and then while feeding, and skip the rest of the file. `fa_scan_finish` still gives the result.
- **The same numbers.** Packets or decoded frames of the same audio give the same result to the last digit, and within 0.02 LU of a scan by path (which cuts the file into chunks). A packet that cannot be decoded returns its error and is skipped; keep feeding.
- **Start it before playback.** A scan that starts when the song starts playing only helps if it is already well on its way when the wait runs out; a 4-minute song takes a fraction of a second of CPU, a film takes minutes and a second read of the file. For long files prefer tags, the cache or the Dolby dialogue level.
- **What a stream sees while the feed runs.** Its `norm_pending` is set, so the first play waits (at most the wait, plus once more when more than 75% is fed, which needs `duration_seconds`). The feed goes in order, so a value from its first minutes would lean on the intro: it is offered as `FA_LOUDNESS_PARTIAL` only after a quarter of the song (30 s when the duration is not given); before that the running estimate is used. `fa_scan_finish` hands the result to every open stream of the song, which glide to it; closing the feed without finishing it makes them stop waiting.
- **Without a cache directory** a fed scan still gives its result to `fa_scan_finish` and to the streams open at that moment; nothing is stored.

### Live streams and app I/O

Streams that can't be read a second time (`fa_stream_open_io` and [web streams](web-streams.md)) are never scanned. (A [packet or push stream](#streams-you-feed-packets-and-decoded-frames) can be, if your app feeds a scan.) They use the running estimate, which adapts as the program changes. You can still give them a value with `fa_stream_set_loudness`.

## The cache

Each song is measured once, ever. Songs with ReplayGain or R128 tags never use the cache: tags are read first.

- **Where.** You do not have to set anything: with `fa_engine_config.cache_directory` NULL (.NET `AudioEngineOptions.CacheDirectory` null) FatzMedia.Audio uses the OS's per-user cache folder plus `FatzAudio`: Windows `%LOCALAPPDATA%\FatzAudio`, Linux `$XDG_CACHE_HOME` or `~/.cache`, macOS and iOS `~/Library/Caches`, Android the app's cache folder. A folder you set always wins (for example your app's own `AudioCache`). It is created with all its parents. `FA_ENGINE_NO_CACHE` (.NET `NoCache = true`) turns the cache off: nothing is kept or written.
- **When it cannot be used.** The cache is only an optimisation, so a folder that cannot be made, or a file that cannot be written (read-only, disk full, no permission), never fails the engine or a scan; songs are just measured again. `fa_engine_get_cache_info` (.NET `engine.GetCacheInfo()`) tells you: `status` is `FA_OK`, `FA_ERROR_IO` (folder or file unusable, also after a later failed write) or `FA_ERROR_NOT_FOUND` (no cache). Check it once after creating the engine and log it:

```c
fa_cache_info ci = { sizeof(ci) };
fa_engine_get_cache_info(engine, &ci);
if (ci.status != FA_OK) log("loudness cache off: %d (%s)", ci.status, ci.directory ? ci.directory : "none");
```

- **One file.** `loudness.fac` holds 48 bytes per song. It is read into memory once, on the first lookup, on a scan thread (never the audio thread): a full 5 MB file (109,000 songs) takes about 16 ms, and each lookup after that takes microseconds. New results are appended.
- **Size limit.** 5 MB by default; change it with `fa_engine_set_cache_size(engine, bytes)` (.NET `SetCacheSize`, or `AudioEngineOptions.CacheMaxBytes`), at least 16 KB. When the file outgrows the limit it is rewritten, keeping the songs **used** most recently within 3/4 of it. Used means found by a lookup or measured by a scan, not when the entry was written, so a song played often is never the one dropped. A hit updates the time in memory at once, and in the file at most once a day per song (and in full at every rewrite).
- **Clear.** `fa_engine_clear_cache` (.NET `ClearCache()`) deletes the cached loudness and the cached [track analysis](analysis.md) (FatzMedia.Audio's own files in the folder only). Songs are measured again as they are played.
- **Shared.** Engines in one process that use the same folder share one cache in memory, so four players cost one copy and one file read. Between processes (two apps on one folder), every read and write of the file happens under a lock on `loudness.fac.lock` beside it, and each result is appended in one write, so no app loses another's results; when one app rewrites the file it first merges what the others appended. The size limit and `fa_engine_clear_cache` apply to every engine sharing the cache. After a clear, another app that is running keeps its copy in memory and may write it back.
- **Never trusted.** A file of another version, or with a damaged header, is dropped and written again. Each record carries a marker and a checksum: one cut short by a crash, or damaged, is skipped and the file is rewritten clean. Rewrites go to a temporary file that replaces the old one by rename, so a crash leaves one whole file or the other. If the lock can't be had within 2 seconds (a stuck app), FatzMedia.Audio writes anyway: the worst case is a song measured again.
- **The fingerprint** covers the audio data only, not the file's name or its tags:

| Format | What is fingerprinted |
|---|---|
| WAV, RF64, AIFF | The format chunk and the whole `data` / `SSND` chunk, so an edit in place that keeps the length is noticed. Over 64 MB (about 6 minutes of CD audio): 64 points plus the file's last-write time instead, so a cached hour-long WAV isn't read whole (600 MB) on every play; a new LIST tag then costs one scan |
| MP4, M4A, MOV | The `mdat` box (the tags are in `moov`), at 16 points |
| FLAC | The frames after the metadata blocks (comments, pictures, padding), at 16 points |
| Ogg Vorbis, Opus, FLAC | From the first audio page (the comment header is before it), at 16 points |
| MP3, AAC and the rest | The file without ID3v2 at the start and ID3v1, APEv2 or Lyrics3 at the end, at 16 points |

"16 points" means 4 KB read at 16 places spread evenly over the audio, plus its length: a coded stream changes throughout when it is encoded again, so this is enough and stays fast for multi-GB video files. A MIDI file's key also includes the soundfont and rate it plays with. Apps can compute the same fingerprint themselves with `fa_fingerprint_path` / `fa_fingerprint_memory` (.NET `AudioFingerprint.Compute`), for example to tell a retagged song from a changed one in a library ([audio fingerprint](tags.md#audio-fingerprint)). Results cached by 1.0.0-rc.3 or earlier (one small `.loudness` file per song) are not read, so after upgrading each song is measured once more the first time it plays; `fa_engine_clear_cache` deletes the old files.

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

## Writing loudness tags

To store the loudness in the files themselves, so other players (and FatzMedia.Audio on another machine) know it without a scan, call `fa_tags_write_loudness`. It is never automatic: nothing writes to your files unless you call it. It always measures (old loudness tags and the cache are ignored, and the cache is neither read nor written), then writes:

| Format | Tags written |
|---|---|
| MP3, ADTS AAC | ID3v2 `TXXX` frames (an existing ID3v2.3 or 2.4 tag keeps its version; a new tag is ID3v2.3). An APEv2 tag the file already has gets the same values. |
| FLAC, Ogg Vorbis | Vorbis comments |
| Ogg Opus | `R128_TRACK_GAIN` / `R128_ALBUM_GAIN` (RFC 7845: Q7.8 dB to -23 LUFS, on top of the header's output gain), never ReplayGain keys |
| MP4, M4A, M4B | iTunes freeform items `----:com.apple.iTunes:replaygain_track_gain` etc. |
| Monkey's Audio, WavPack, Musepack, TTA | APEv2 |
| WAV, AIFF, CAF, WMA, Matroska, MIDI, trackers, ZIP entries | not written: `FA_ERROR_UNSUPPORTED_FORMAT`, the file is left alone |

All but Opus get ReplayGain 2: `REPLAYGAIN_TRACK_GAIN` (`"-7.89 dB"`, to -18 LUFS) and `REPLAYGAIN_TRACK_PEAK` (linear; FatzMedia.Audio writes the true peak, which is never below the sample peak, so a player's clipping guard stays safe), plus the album pair with `FA_TAGS_WRITE_ALBUM`. Older loudness tags of the kinds written are replaced in both spellings (a stale `R128_TRACK_GAIN` in a FLAC file cannot win over the new ReplayGain value); every other tag, picture and the audio stay byte for byte. A track-only write keeps album tags that are already there.

C:
```c
const char* album[] = { "01.flac", "02.flac", "03.flac" };
fa_loudness_tag_result res[3];
for (int i = 0; i < 3; i++) res[i].struct_size = sizeof(res[i]);
if (fa_tags_write_loudness(engine, album, 3, FA_TAGS_WRITE_ALBUM, res) != FA_OK)
    for (int i = 0; i < 3; i++)
        if (res[i].result != FA_OK) printf("%s: %d\n", album[i], res[i].result);
```

C#:
```csharp
var results = await Task.Run(() => engine.WriteLoudnessTags(albumFiles, album: true));
foreach (var r in results.Where(r => !r.Written))
    Console.WriteLine($"{r.Path}: {r.Error}");

engine.WriteLoudnessTags("single.mp3");   // one file; throws FatzAudioException if it was not written
```

**Safety.** Each file is written to a new file next to it (`<name>.fatzaudio-N.tmp`), flushed to the disk, and checked: its loudness tags must read back as written, and its first and middle audio must decode to exactly the same samples as the original's. Only then is it renamed over the original, so a failure, a crash or a power cut leaves the original as it was (at worst a stray `.tmp` file next to it). The original must not change while it is measured and written (`FA_ERROR_BUSY` if it did). On POSIX the new file gets the original's permissions and a symbolic link is followed (the file it points to is rewritten). On Windows the rename fails with `FA_ERROR_IO` while the file is open in another program, or in a playing stream, since FatzMedia.Audio opens files without delete sharing; close it first.

**Album.** With `FA_TAGS_WRITE_ALBUM` the files are one programme: the album loudness is measured over all of their audio together (EBU R128 gating over the whole album), the album peak is the highest true peak. If any file cannot be measured, nothing is written: that file reports its error and the others `FA_ERROR_CANCELLED`.

**What is refused.** ID3v2.2 tags, ID3v2 tags or APEv2 tags it cannot parse in full, FLAC files with two comment blocks, Ogg files with several streams multiplexed or audio on a header page, Ogg FLAC and Speex, fragmented MP4 whose moov would change size, and files too short to have a loudness (under 0.4 s, or digital silence: `FA_ERROR_NOT_FOUND`). Each of these is left as it was. A chained Ogg file gets the tags in its first stream only.

## Threads

- Scans run on their own scan threads, never on the decode workers or the audio thread. `fa_scan_loudness_path` also uses the calling thread. A fed scan (`fa_scan_feed`) runs only on the thread that pushes into it.
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
