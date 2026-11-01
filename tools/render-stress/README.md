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

It prints, per stream, the dropouts (`fa_stream_stats.underruns`), how far the stream clock fell behind wall time
times the tempo, and the least decoded audio seen ahead (`buffered`); per surface, the frames drawn, how many came back
`behind`, and the time `update_into` took on the drawing thread.

Pin it to a few cores to make the machine busy, with burners on top:

```sh
taskset -c 0-3 build/bin/fa_render_stress --colours 100 --burn 2 --soundfont TimGM6mb.sf2
```

On Windows, `start /affinity F build\bin\Release\fa_render_stress.exe ...` does the same.

## Results (2026-11-01, Linux, 4 cores pinned, 2 burners, a soundfont)

Before step K1 (main at cfeb65a, with only `fa_stream_get_stats` added so the tool runs) and after it, render-ahead on:

| Run | Build | Dropouts | Least buffered (ms) | Surfaces `behind` |
|---|---|---|---|---|
| `--colours 100`, 30 s, 3 runs | Before | 0, 0, 0 | 80-90 | 0-10 % |
| | After | 0, 0, 0 | 80-90 | 4-17 % |
| `--colours 100 --workers 2 --lyrics 4`, 20 s | Before | 0 | 60 | 0 % |
| | After | 0 | 80 | 0 % |
| `--colours 100 --workers 4 --lyrics 6 --extra 1`, 20 s | Before | 0 | 40-50 | 0 % |
| | After | 0 | 83-90 | 0 % |

Linux's scheduler shares the cores fairly enough that these loads did not reproduce MediaControl's dropouts (measured
on Windows): one early run before the change had 2 dropouts and 10 ms left, and its repeats had none. What the change
shows here is the headroom: with drawing work on every worker the streams fell to 40-60 ms ahead before it and stay
at 80-90 ms after it, and the drawing thread got more frames done in the heavy runs (45 against 32, 21 against 15).
This machine's RLIMIT_NICE is 0, so the lower priority was not in play: these numbers are the top-up and the free
worker alone. The white-box test
`test_drawing_leaves_a_worker_to_decode` (tests/test_internal.c) shows the cause directly: before the change a shared
drawing job took every worker.
