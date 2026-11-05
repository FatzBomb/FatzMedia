/* fa_archive_bench: time to the first decoded audio of a song inside an archive, through the public API
 * (tools/archive-bench/README.md). It prints one JSON line.
 *
 *   fa_archive_bench <archive> [--entry name] [--reps N] [--no-cdg]
 *   fa_archive_bench <archive> --sequence name1,name2,...
 *
 * First form: N cold opens (the engine's archive cache off), each open + the first 4096 decoded frames, then one
 * open after another with the cache on ("reopen"). Memory is the process's growth over its state before the last
 * cold open (Linux only). Second form: the named entries opened one after another with the cache on, as a playlist
 * does; each time is open + first frames. Uses no FFmpeg; the song's graphics are loaded if the archive has them. */
#if !defined(_WIN32) && !defined(_POSIX_C_SOURCE)
#  define _POSIX_C_SOURCE 200809L
#endif
#include "fatzaudio/fatzaudio.h"
#include "../bass-compare/bench_common.h"

#define FIRST_FRAMES 4096

static fa_engine* make_engine(int archive_cache)
{
    fa_engine_config c;
    fa_engine* e = NULL;
    fa_engine_config_init(&c);
    c.flags = FA_ENGINE_NULL_OUTPUT | FA_ENGINE_NO_CACHE | (archive_cache ? 0 : FA_ENGINE_NO_ARCHIVE_CACHE);
    if (fa_engine_create(&c, &e) != FA_OK) { printf("{\"error\":\"engine\"}\n"); exit(1); }
    return e;
}

/* Opens, decodes the first frames, closes. Returns milliseconds from the call to the first audio. */
static double first_pcm(fa_engine* e, const char* path, const char* entry, int no_cdg, double* peak_kb)
{
    fa_open_options o;
    fa_stream* s = NULL;
    fa_stream_info info = { .struct_size = sizeof(info) };
    float buf[FIRST_FRAMES * 8];
    fa_frames got = 0;
    fa_result r;
    double t0 = bench_now_ms(), t;
    fa_open_options_init(&o);
    o.flags = FA_OPEN_DECODE_ONLY | FA_OPEN_NO_NORMALIZE | (no_cdg ? FA_OPEN_NO_CDG : 0);
    o.archive_entry = entry;
    r = fa_stream_open_path(e, path, &o, &s);
    if (r != FA_OK) {
        printf("{\"error\":\"%s\"}\n", fa_result_string(r));
        exit(1);
    }
    fa_stream_get_info(s, &info);
    if (info.format.channels > 8) info.format.channels = 8;
    r = fa_stream_read(s, buf, FIRST_FRAMES, &got);
    t = bench_now_ms() - t0;
    if (peak_kb) *peak_kb = bench_peak_kb();
    if (r != FA_OK || got == 0) { printf("{\"error\":\"no audio: %s\"}\n", fa_result_string(r)); exit(1); }
    fa_stream_close(s);
    return t;
}

static int cmp(const void* a, const void* b) { double x = *(const double*)a, y = *(const double*)b; return (x > y) - (x < y); }

static double median(double* v, int n) { qsort(v, (size_t)n, sizeof(double), cmp); return v[n / 2]; }

int main(int argc, char** argv)
{
    const char *path, *entry = NULL, *seq = NULL;
    int reps = 5, no_cdg = 0, i;
    if (argc < 2) { fprintf(stderr, "usage: fa_archive_bench <archive> [--entry name] [--reps N] [--no-cdg] | --sequence a,b,c\n"); return 2; }
    path = argv[1];
    for (i = 2; i < argc; i++) {
        if (!strcmp(argv[i], "--entry") && i + 1 < argc) entry = argv[++i];
        else if (!strcmp(argv[i], "--reps") && i + 1 < argc) reps = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--sequence") && i + 1 < argc) seq = argv[++i];
        else if (!strcmp(argv[i], "--no-cdg")) no_cdg = 1;
    }
    if (reps < 1) reps = 1;
    if (reps > 100) reps = 100;
    if (seq) {
        char* list = (char*)malloc(strlen(seq) + 1);
        char* p;
        fa_engine* e = make_engine(1);
        int first = 1;
        strcpy(list, seq);
        printf("{\"archive\":\"%s\",\"sequence_ms\":[", path);
        for (p = strtok(list, ","); p; p = strtok(NULL, ",")) {
            printf("%s%.2f", first ? "" : ",", first_pcm(e, path, p, no_cdg, NULL));
            first = 0;
        }
        printf("]}\n");
        fa_engine_destroy(e);
        free(list);
        return 0;
    }
    {
        double t[100], base, peak = 0, cold, reopen;
        fa_engine* e = make_engine(0);
        first_pcm(e, path, entry, no_cdg, NULL); /* page cache, code pages, workers */
        for (i = 0; i < reps; i++) {
            base = bench_mem_start();
            t[i] = first_pcm(e, path, entry, no_cdg, &peak);
        }
        cold = median(t, reps);
        fa_engine_destroy(e);
        e = make_engine(1);
        first_pcm(e, path, entry, no_cdg, NULL);
        for (i = 0; i < reps; i++) t[i] = first_pcm(e, path, entry, no_cdg, NULL);
        reopen = median(t, reps);
        fa_engine_destroy(e);
        printf("{\"archive\":\"%s\",\"cold_ms\":%.2f,\"reopen_ms\":%.2f,\"added_kb\":%.0f}\n", path, cold, reopen, peak - base);
    }
    return 0;
}
