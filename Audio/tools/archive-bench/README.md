# archive-bench

Measures how long a song takes to reach its first decoded audio when it sits inside an archive, through the public
C API (`fa_stream_open_path` with a decode-only stream, null output, no loudness cache). It is a measuring tool, not a CI
test; the figures it produced are in [Archives, "Speed"](../../docs/features/archives.md#speed).

```
fa_archive_bench <archive> [--entry name] [--reps N] [--no-cdg]
fa_archive_bench <archive> --sequence name1,name2,...
python3 tools/archive-bench/run.py build/bin/fa_archive_bench [--work DIR] [--reps N] [--json out.json]
```

`fa_archive_bench` prints one JSON line. The first form opens the song `N` times (default 5) with the engine's
archive cache off and reports the median time from the call to the first 4096 decoded frames (`cold_ms`), then
the same with the cache on (`reopen_ms`, the song opened again after it closed), and the process's memory growth over
its state before the last cold open (`added_kb`, Linux only). Page cache, code pages and worker threads are warmed by
one open first, so the numbers are what a second song costs, not the first run after a boot. The second form opens the
named entries one after the other with the cache on, as a playlist does, and prints each time (`sequence_ms`).

`run.py` makes the test files (needs ffmpeg with libmp3lame, `zip`, `tar`, `gzip`, `bzip2`, `xz` and the `py7zr`
package) and runs every row of the docs table: a 4-minute MP3 at 192 kbps plus a 1.7 MB `.cdg` with 12% non-empty packs, as
stored and DEFLATE zip, tar, `.tar.gz`, `.tar.bz2`, `.tar.xz` (one block and `xz -T`), and 7z; and ten such songs
in one solid archive of each kind, for the 1st and 10th song. py7zr writes LZMA2 at preset 5 with no dictionary
resets, like 7-Zip's single-thread default; a real 7-Zip makes bigger files differently.

The times depend on the machine: the docs give them for a 4-core 2.1 GHz cloud Xeon; a current desktop is about 1.7-2x faster.
