# render-stress

Measures whether drawing karaoke surfaces (CD+G and lyrics at 2160p with every effect, render-ahead on) takes the
decode workers away from the audio ([CD+G render-ahead](../../docs/features/cdg.md#drawing-into-your-own-buffers-fa_surface_update_into),
[dropouts](../../docs/features/streams.md#dropouts-underruns)). It is a measuring tool, not a CI test.

`fa_render_stress` plays two streams on the null output with tempo on (1.05 and 0.95, so each keeps a 150 ms buffer),
gives each two CD+G surfaces at full effects (one traced at smoothness 9, one at smoothness 2 with edge blend), and
draws every surface 60 times a second on one thread with `fa_surface_update_into`, as a player's render loop would.
With `--soundfont`, the second stream is a MIDI song with lyrics and its second surface is a lyric surface. Audio,
graphics and MIDI are generated, so it needs no files.

```
fa_render_stress [--seconds N] [--render-ahead 0|1] [--workers N] [--burn N] [--size WxH]
                 [--tempo T] [--soundfont file.sf2] [--extra N] [--latency ms] [--colours ms]
                 [--lyrics N] [--threads] [--events ms] [--ramp]
```

| Option | Default | What it does |
|---|---|---|
| `--seconds` | 30 | How long to play |
| `--render-ahead` | 1 | Render-ahead on the surfaces |
| `--workers` | 0 (cores) | `fa_engine_config.worker_threads` |
| `--burn` | 0 | Busy threads standing in for the app's own work (video decoding, its UI) |
| `--size` | 3840x2160 | Presentation size of every surface |
| `--tempo` | 1.05 / 0.95 | Tempo of both streams |
| `--soundfont` | none | A GM soundfont: the second stream becomes MIDI with a lyric surface |
| `--extra` | 0 | More CD+G surfaces per stream |
| `--latency` | 150 | `fa_tempo_options.latency_ms`: how much each stream keeps decoded ahead |
| `--colours` | 10000 | How often the CD+G loads new colours, in ms. Each load redraws the whole picture with its effects (the 50-220 ms frames); a real song does it rarely, 100 makes it a stress test |
| `--lyrics` | 1 | With a soundfont: how many lyric surfaces the second stream has (presets in turn) |
| `--threads` | off | Draw every surface on a thread of its own, as MediaControl's slice-0 spike did |
| `--events` | 0 (none) | Every so many ms, in turn: both streams seek (back to half way, or a quarter further on), or the drawing thread resizes every surface to 3/4 and back (`fa_surface_set_presentation_size`, then `fa_surface_get_layout` and new buffers on `FA_ERROR_SIZE_MISMATCH`) |
| `--ramp` | off | MediaControl's DJ-sync ramp on both streams, over and over: 1 s steady, the tempo times 1.0 to 1.3 over 3 s, back over 1 s. The clock lag is then measured against the tempo set, so it includes the tempo latency (about 60 ms at the steepest part) |

It prints, per stream, the dropouts (`fa_stream_stats.underruns`), how far the stream clock fell behind wall time
times the tempo, and the least decoded audio seen ahead (`buffered`); per surface, the frames drawn, how many came back
`behind` (and of those, how many had nothing new yet), the time `update_into` took on the drawing thread, and how far
the frame shown was behind the clock; with `--events`, the longest `fa_surface_set_presentation_size` and
`fa_surface_get_layout`.

Pin it to a few cores to make the machine busy, with burners on top:

```sh
taskset -c 0-3 build/bin/fa_render_stress --colours 100 --burn 2 --soundfont TimGM6mb.sf2
```

On Windows, `start /affinity F build\bin\Release\fa_render_stress.exe ...` does the same.

## Results (2026-11-01, Linux, 4 cores pinned, a soundfont)

Before step K1 (main at cfeb65a, with only `fa_stream_get_stats` added so the tool runs) and after it, render-ahead on,
30 s each. `--threads` draws every surface on a thread of its own, as MediaControl's slice-0 spike did (it lost
170-580 ms of clock in 30 s on a 4-vCPU Linux machine):

| Run | Build | Dropouts (stream 0 / 1) | Clock lag, max | Least buffered (stream 0 / 1) |
|---|---|---|---|---|
| `--threads --tempo 1.0` | Before | 0 / 65 | 652 ms | 190 / 0 ms |
| | After | 0 / 0 | 2.3 ms | 340 / 80 ms |
| `--threads --colours 100` (tempo 1.05 / 0.95) | Before | 158 / 157 | 1660 ms | 0 / 0 ms |
| | After | 0 / 0 | 2.0 ms | 90 / 90 ms |
| `--threads --tempo 1.0 --lyrics 2 --colours 1000` | Before | 8 / 72 | 722 ms | 0 / 0 ms |
| | After | 0 / 0 | 2.7 ms | 360 / 90 ms |

With one drawing thread for all surfaces (no `--threads`, 2 burners) neither build dropped out, but under heavy
drawing (`--workers 4 --lyrics 6 --extra 1`) the least buffered went from 40-50 ms before to 83-90 ms after.

This machine's RLIMIT_NICE is 0, so the lower priority was not in play: these numbers are the top-up and the free
worker alone. The white-box test `test_drawing_leaves_a_worker_to_decode` (tests/test_internal.c) shows the cause
directly: before the change a shared drawing job took every worker.

## Results (2026-11-02, step K2: update_into never waits)

Linux, 4 cores pinned, the TimGM6mb soundfont, `--threads --soundfont TimGM6mb.sf2`, 20 s each, main before K2 (with this
version of the tool) against K2. Every surface is 2160p with every effect; the CD+G surfaces are 3020x2180.

| Run | Build | `update_into` max (per surface) | Over 8 ms | Frames drawn of ~1200 | Shown late (`behind`) | Behind the clock, average |
|---|---|---|---|---|---|---|
| plain | Before | 146-191 ms | 35-441 | 947-1138 | 9-65% | 8-34 ms |
| | After | 16-36 ms | 2-3 | 1199-1201 | 20-44% | 12-17 ms |
| `--events 1500 --colours 1000 --burn 1` (seeks, resizes, a full redraw a second) | Before | 356-570 ms | 35-295 | 839-982 | 14-46% | 9-23 ms |
| | After | 24-31 ms | 5-10 | 1185-1191 | 44-56% | 24-34 ms |

What is left on the drawing thread is the copy into a buffer (a whole 2160p buffer is about 25 MB; the longest calls
are whole copies into freshly allocated buffers right after a resize). After a resize the surface has nothing new for
10-20 calls while a worker draws the first frame of the new layout; a player keeps its old texture meanwhile.
`set_presentation_size` still waits for a render in progress (up to 22 ms here, up to a 2160p frame in general);
`get_layout` right after `FA_ERROR_SIZE_MISMATCH` took under 0.1 ms. No run dropped out in either build.

## Results (2026-11-02, step K3: lyric render-ahead through tempo ramps)

Linux, 4 cores pinned, the TimGM6mb soundfont, `--threads --ramp --soundfont TimGM6mb.sf2`, 20 s each. Lyric surface
only (the CD+G surfaces are keyed by pack position and do not change). "Before" is K2, where a lyric frame of another
0.25% tempo step counted as late; "after" lets a frame within 2% of the current tempo count as on time.

| Size | Build | Lyric frames shown late (`behind`) | Without a ramp |
|---|---|---|---|
| 1920x1080 | Before | 655 of 1201 (55%) | 18 of 1200 (2%) |
| | After | 27 of 1200 (2%) | |
| 3840x2160, every effect | Before | 1047 of 1201 (87%) | 333 of 1200 (28%) |
| | After | 484-570 of 1200 (40-48%) | |

At 1080p the ramp no longer shows: as many frames are late as without it. At 2160p the workers are 268% busy and what
is left is frames late by time, not tempo (a 4% tolerance gave the same 42%). No run dropped out.
