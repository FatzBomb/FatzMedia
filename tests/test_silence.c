/* Silence detection: the rule (fa_silence_detector), the per-stream edge scan
 * (fa_stream_get_silence) in each of its paths, FA_ANALYZE_SILENCE and its cache,
 * and MIDI tails. Every answer is checked against audio made here with known
 * silence, and the edge scan against a full decode of the same file. */
#include "midi_synth_test.h"
#include <time.h>
#if defined(_WIN32)
#  include <direct.h>
#  include <io.h>
#else
#  include <dirent.h>
#  include <unistd.h>
#endif

#define CHECK_EQ(a, b) do { long long va_ = (long long)(a), vb_ = (long long)(b); if (va_ != vb_) { \
    fprintf(stderr, "  FAIL %s:%d: %s == %s (%lld vs %lld)\n", __FILE__, __LINE__, #a, #b, va_, vb_); g_failures++; return; } } while (0)

#define LOW_RATE 8000   /* long test files stay small */
#define MAX_REGIONS 16

typedef struct result {
    fa_silence info;
    fa_silence_region regs[MAX_REGIONS];
    uint32_t n;
} result;

/* ---- audio made here ---------------------------------------------------------------- */

/* Mono audio of `seconds`, silent except a 440 Hz tone at 0.5 in each [on, off) span
 * (seconds, in pairs). */
static float* tones(double seconds, uint32_t rate, const double* spans, int nspans, fa_frames* out_n)
{
    fa_frames n = (fa_frames)(seconds * rate + 0.5), i;
    float* x = (float*)calloc((size_t)n, sizeof(float));
    int k;
    for (k = 0; k < nspans; k++) {
        fa_frames a = (fa_frames)(spans[2 * k] * rate + 0.5), b = (fa_frames)(spans[2 * k + 1] * rate + 0.5);
        for (i = a; i < b && i < n; i++) x[i] = 0.5f * (float)sin(2.0 * PI * 440.0 * (double)(i - a) / rate + 0.3);
    }
    *out_n = n;
    return x;
}

static buf make_wav(const float* x, fa_frames n, uint32_t rate)
{
    buf b = { 0 };
    fa_frames i;
    put(&b, "RIFF", 4); put32le(&b, (uint32_t)(36 + n * 2)); put(&b, "WAVE", 4);
    put(&b, "fmt ", 4); put32le(&b, 16); put16le(&b, 1); put16le(&b, 1);
    put32le(&b, rate); put32le(&b, rate * 2); put16le(&b, 2); put16le(&b, 16);
    put(&b, "data", 4); put32le(&b, (uint32_t)(n * 2));
    for (i = 0; i < n; i++) {
        double v = x[i] * 32767.0;
        put16le(&b, (unsigned)(uint16_t)(int16_t)(v < 0 ? v - 0.5 : v + 0.5));
    }
    return b;
}

static fa_stream* open_mem(fa_engine* e, const buf* b)
{
    fa_open_options o;
    fa_stream* s = NULL;
    fa_open_options_init(&o);
    o.flags = FA_OPEN_DECODE_ONLY;
    o.soundfont = g_font;
    if (fa_stream_open_memory(e, b->d, b->n, &o, &s) != FA_OK) return NULL;
    return s;
}

static fa_result stream_silence(fa_stream* s, const fa_silence_options* o, result* r)
{
    memset(r, 0, sizeof(*r));
    r->info.struct_size = sizeof(r->info);
    return fa_stream_get_silence(s, o, &r->info, r->regs, MAX_REGIONS, &r->n);
}

static fa_result analysis_silence(fa_stream* s, uint32_t flags, result* r)
{
    fa_analysis_options o;
    fa_analysis* a = NULL;
    fa_result rr;
    memset(r, 0, sizeof(*r));
    fa_analysis_options_init(&o);
    o.kinds = FA_ANALYZE_SILENCE;
    o.flags = flags;
    rr = fa_analyze_stream(s, &o, &a);
    if (rr != FA_OK) return rr;
    r->info.struct_size = sizeof(r->info);
    rr = fa_analysis_get_silence(a, &r->info, r->regs, MAX_REGIONS, &r->n);
    fa_analysis_free(a);
    return rr;
}

static fa_frames sec(double s, uint32_t rate) { return (fa_frames)(s * rate + 0.5); }

/* The regions match the spans in seconds: a region ends exactly where a tone starts
 * (its first sample is 0.5 * sin(0.3)), and starts within 2 frames of where one ends
 * (a sine's last samples can be near a zero crossing). */
static int regions_are(const result* r, const double* spans, int n, uint32_t rate)
{
    int i;
    if ((int)r->n != n) return 0;
    for (i = 0; i < n; i++) {
        fa_frames a = sec(spans[2 * i], rate), b = sec(spans[2 * i + 1], rate);
        if (r->regs[i].start > a || r->regs[i].start < a - 2 || r->regs[i].end != b) return 0;
    }
    return 1;
}

/* ---- the rule ---------------------------------------------------------------------------- */

static void test_detector_rule(void)
{
    /* 1.5 s silence, tone to 3.5, a 0.5 s gap (too short), tone to 5, a 3 s gap, tone to
     * 9, 2 s of silence. */
    static const double spans[] = { 1.5, 3.5, 4.0, 5.0, 8.0, 9.0 };
    static const double want[] = { 0.0, 1.5, 5.0, 8.0, 9.0, 11.0 };
    fa_audio_format f = { FA_FORMAT_F32, RATE, 1, 0 };
    fa_silence_detector* d = NULL;
    fa_frames n, at = 0;
    float* x = tones(11.0, RATE, spans, 3, &n);
    int step = 1;
    result r;
    CHECK(fa_silence_detector_create(&f, NULL, &d) == FA_OK);
    while (at < n) {   /* odd sizes, from 1 frame up */
        fa_frames k = n - at < step ? n - at : step;
        const void* p = x + at;
        CHECK(fa_silence_detector_feed(d, &p, k) == FA_OK);
        at += k;
        step = step * 3 + 1;
    }
    memset(&r, 0, sizeof(r));
    r.info.struct_size = sizeof(r.info);
    CHECK(fa_silence_detector_get(d, &r.info, r.regs, MAX_REGIONS, &r.n) == FA_OK);
    fa_silence_detector_free(d);
    free(x);
    CHECK_EQ(r.info.first_audible, sec(1.5, RATE));
    CHECK(r.info.last_audible <= sec(9.0, RATE) && r.info.last_audible >= sec(9.0, RATE) - 2);
    CHECK_EQ(r.info.length, n);
    CHECK(regions_are(&r, want, 3, RATE));
}

/* Exactly at the threshold is audible, just under it is not; all formats agree. */
static void test_detector_threshold(void)
{
    const float thr = (float)pow(10.0, -40.0 / 20.0);
    float x[3 * RATE];
    short s16[3 * RATE];
    float planar_l[3 * RATE], planar_r[3 * RATE];
    fa_silence_options o;
    fa_silence_detector* d = NULL;
    result r, r2;
    int i;
    fa_silence_options_init(&o);
    o.threshold_db = -40.0f;
    o.min_length_ms = 500;
    for (i = 0; i < 3 * RATE; i++) x[i] = (i % 2 ? -1.0f : 1.0f) * thr * 0.999f;
    x[RATE] = thr;            /* one audible frame, alone, at 1 s */
    x[2 * RATE + 7] = -thr;   /* and one at 2 s + 7 */
    {
        fa_audio_format f = { FA_FORMAT_F32, RATE, 1, 0 };
        const void* p = x;
        CHECK(fa_silence_detector_create(&f, &o, &d) == FA_OK);
        CHECK(fa_silence_detector_feed(d, &p, 3 * RATE) == FA_OK);
        memset(&r, 0, sizeof(r));
        r.info.struct_size = sizeof(r.info);
        CHECK(fa_silence_detector_get(d, &r.info, r.regs, MAX_REGIONS, &r.n) == FA_OK);
        fa_silence_detector_free(d);
    }
    CHECK_EQ(r.info.first_audible, RATE);
    CHECK_EQ(r.info.last_audible, 2 * RATE + 8);
    CHECK_EQ(r.info.length, 3 * RATE);
    CHECK_EQ(r.info.flags, 0);
    CHECK_EQ(r.n, 3);
    CHECK(r.regs[0].start == 0 && r.regs[0].end == RATE);
    CHECK(r.regs[1].start == RATE + 1 && r.regs[1].end == 2 * RATE + 7);
    CHECK(r.regs[2].start == 2 * RATE + 8 && r.regs[2].end == 3 * RATE);   /* still going, already long enough */

    /* S16 interleaved stereo and F32 planar stereo: the louder channel counts. */
    for (i = 0; i < 3 * RATE; i++) { planar_l[i] = 0.0f; planar_r[i] = x[i]; }
    {
        fa_audio_format f = { FA_FORMAT_F32_PLANAR, RATE, 2, 0 };
        const void* p[2];
        p[0] = planar_l; p[1] = planar_r;
        CHECK(fa_silence_detector_create(&f, &o, &d) == FA_OK);
        CHECK(fa_silence_detector_feed(d, p, 3 * RATE) == FA_OK);
        memset(&r2, 0, sizeof(r2));
        r2.info.struct_size = sizeof(r2.info);
        CHECK(fa_silence_detector_get(d, &r2.info, r2.regs, MAX_REGIONS, &r2.n) == FA_OK);
        fa_silence_detector_free(d);
        CHECK(r2.n == r.n && memcmp(r2.regs, r.regs, r.n * sizeof(r.regs[0])) == 0);
        CHECK_EQ(r2.info.first_audible, r.info.first_audible);
    }
    {
        /* -40 dB is 327.67 in 16 bits: 328 is audible, 327 is not. */
        fa_audio_format f = { FA_FORMAT_S16, RATE, 1, 0 };
        const void* p = s16;
        for (i = 0; i < 3 * RATE; i++) s16[i] = (short)(i % 2 ? -327 : 327);
        s16[RATE] = 328;
        CHECK(fa_silence_detector_create(&f, &o, &d) == FA_OK);
        CHECK(fa_silence_detector_feed(d, &p, 3 * RATE) == FA_OK);
        memset(&r2, 0, sizeof(r2));
        r2.info.struct_size = sizeof(r2.info);
        CHECK(fa_silence_detector_get(d, &r2.info, r2.regs, MAX_REGIONS, &r2.n) == FA_OK);
        fa_silence_detector_free(d);
        CHECK_EQ(r2.info.first_audible, RATE);
        CHECK_EQ(r2.info.last_audible, RATE + 1);
        CHECK_EQ(r2.n, 2);
    }
}

static void test_args(void)
{
    fa_audio_format f = { FA_FORMAT_F32, RATE, 1, 0 };
    fa_silence_options o;
    fa_silence_detector* d = NULL;
    fa_silence info;
    uint32_t n = 99;
    fa_silence_options_init(&o);
    CHECK(o.threshold_db == -60.0f && o.min_length_ms == 1000);
    o.threshold_db = 1.0f;
    CHECK(fa_silence_detector_create(&f, &o, &d) == FA_ERROR_INVALID_ARG && !d);
    fa_silence_options_init(&o);
    o.min_length_ms = 0;
    CHECK(fa_silence_detector_create(&f, &o, &d) == FA_ERROR_INVALID_ARG);
    o.struct_size = 4;   /* under the first layout */
    CHECK(fa_silence_detector_create(&f, &o, &d) == FA_ERROR_INVALID_ARG);
    f.channels = 0;
    CHECK(fa_silence_detector_create(&f, NULL, &d) == FA_ERROR_INVALID_ARG);
    f.channels = 1;
    CHECK(fa_silence_detector_create(&f, NULL, &d) == FA_OK);
    CHECK(fa_silence_detector_feed(d, NULL, 10) == FA_ERROR_INVALID_ARG);
    CHECK(fa_silence_detector_feed(d, NULL, 0) == FA_OK);
    CHECK(fa_silence_detector_get(d, NULL, NULL, 1, &n) == FA_ERROR_INVALID_ARG);
    info.struct_size = sizeof(info);
    CHECK(fa_silence_detector_get(d, &info, NULL, 0, &n) == FA_OK);
    CHECK(n == 0 && info.first_audible == FA_FRAMES_UNSET && info.last_audible == FA_FRAMES_UNSET && info.length == 0);
    fa_silence_detector_free(d);
    fa_silence_detector_free(NULL);
    CHECK(fa_stream_get_silence(NULL, NULL, &info, NULL, 0, NULL) == FA_ERROR_INVALID_ARG);
}

/* ---- the stream's edge scan ------------------------------------------------------------- */

/* A file stream against the detector fed the same audio, and the full analysis. */
static void check_file(const double* spans, int nspans, double seconds, int want_complete,
                       const double* want_regions, int nwant)
{
    fa_frames n;
    float* x = tones(seconds, LOW_RATE, spans, nspans, &n);
    buf w = make_wav(x, n, LOW_RATE);
    fa_stream* s = open_mem(g_engine, &w);
    result edge, full;
    clock_t t0;
    double ms;
    CHECK(s);
    t0 = clock();
    CHECK(stream_silence(s, NULL, &edge) == FA_OK);
    ms = (double)(clock() - t0) * 1000.0 / CLOCKS_PER_SEC;
    CHECK(analysis_silence(s, FA_ANALYZE_NO_CACHE, &full) == FA_OK);
    fa_stream_close(s);
    free(w.d);
    free(x);
    printf("  %.0f s file: %u region(s)%s, %.1f ms\n", seconds, edge.n,
           edge.info.flags & FA_SILENCE_COMPLETE ? ", complete" : "", ms);
    CHECK_EQ(full.info.flags, FA_SILENCE_COMPLETE);
    CHECK_EQ(edge.info.flags, want_complete ? FA_SILENCE_COMPLETE : 0);
    CHECK_EQ(edge.info.sample_rate, LOW_RATE);
    CHECK_EQ(edge.info.length, n);
    CHECK_EQ(full.info.length, n);
    CHECK_EQ(edge.info.first_audible, full.info.first_audible);
    CHECK_EQ(edge.info.last_audible, full.info.last_audible);
    if (nspans) {
        CHECK_EQ(edge.info.first_audible, sec(spans[0], LOW_RATE));
        CHECK(edge.info.last_audible >= sec(spans[2 * nspans - 1], LOW_RATE) - 2);
        CHECK(edge.info.last_audible <= sec(spans[2 * nspans - 1], LOW_RATE));
    } else {
        CHECK(edge.info.first_audible == FA_FRAMES_UNSET && edge.info.last_audible == FA_FRAMES_UNSET);
    }
    if (want_complete) {
        CHECK(edge.n == full.n && memcmp(edge.regs, full.regs, edge.n * sizeof(edge.regs[0])) == 0);
    } else {
        /* The leading and the trailing region, and the full scan has them too. */
        CHECK(full.n > edge.n && edge.n == 2);
        CHECK(memcmp(&edge.regs[0], &full.regs[0], sizeof(edge.regs[0])) == 0);
        CHECK(memcmp(&edge.regs[1], &full.regs[full.n - 1], sizeof(edge.regs[0])) == 0);
    }
    if (want_regions) CHECK(regions_are(&full, want_regions, nwant, LOW_RATE));
}

static void test_short_file(void)
{
    /* Under a minute: decoded whole, every region known. */
    static const double spans[] = { 1.5, 3.5, 4.0, 5.0, 8.0, 9.0 };
    static const double want[] = { 0.0, 1.5, 5.0, 8.0, 9.0, 11.0 };
    check_file(spans, 3, 11.0, 1, want, 3);
}

static void test_tail_in_first_window(void)
{
    /* Audio from 2 to 95 s with a gap at 50-60 s, file of 110 s: the inner gap is only
     * in the full scan. */
    static const double spans[] = { 2.0, 50.0, 60.0, 95.0 };
    static const double want[] = { 0.0, 2.0, 50.0, 60.0, 95.0, 110.0 };
    check_file(spans, 2, 110.0, 0, want, 3);
}

static void test_tail_in_later_window(void)
{
    /* Last audio at 100-110 s of 200: the 30 s window is silent, the next one holds it. */
    static const double spans[] = { 2.0, 10.0, 100.0, 110.0 };
    static const double want[] = { 0.0, 2.0, 10.0, 100.0, 110.0, 200.0 };
    check_file(spans, 2, 200.0, 0, want, 3);
}

static void test_long_trailing_silence(void)
{
    /* 300 s of silence after the song: the windows meet the start, every region known. */
    static const double spans[] = { 2.0, 10.0 };
    static const double want[] = { 0.0, 2.0, 10.0, 310.0 };
    check_file(spans, 1, 310.0, 1, want, 2);
}

static void test_all_silent(void)
{
    static const double want[] = { 0.0, 100.0 };
    check_file(NULL, 0, 100.0, 1, want, 1);
}

static void test_not_seekable(void)
{
    fa_audio_format f = { FA_FORMAT_F32, RATE, 2, 0 };
    fa_open_options o;
    fa_stream* s = NULL;
    result r;
    fa_open_options_init(&o);
    o.flags = FA_OPEN_DECODE_ONLY;
    CHECK(fa_stream_open_push(g_engine, &f, &o, &s) == FA_OK);
    CHECK(stream_silence(s, NULL, &r) == FA_ERROR_NOT_SEEKABLE);
    fa_stream_close(s);
}

/* ---- MIDI ------------------------------------------------------------------------------- */

/* The edge scan's tail windows start with a seek, and a MIDI seek plays only the
 * second before its target: a note released earlier but still ringing there is lost.
 * Here a 4 s release (-60 dB after about 2.4 s) starts at 128 s and rings past the
 * window edge at 130 s; the window before reads on until it has died, so the answer
 * matches a full render's. */
static void test_midi_tail(void)
{
    buf sf = make_sf2(NULL, 0, NULL, 0);
    fa_soundfont* font = NULL;
    size_t i;
    fa_open_options o;
    song sg = { 0 };
    buf m;
    fa_stream* s = NULL;
    result edge, full;
    /* The instrument's release generator (38) from -3600 timecents (125 ms) to 2400 (4 s). */
    for (i = 0; i + 4 <= sf.n; i++)
        if (sf.d[i] == 38 && sf.d[i + 1] == 0 && sf.d[i + 2] == 0xF0 && sf.d[i + 3] == 0xF1) { sf.d[i + 2] = 0x60; sf.d[i + 3] = 0x09; break; }
    CHECK(i + 4 <= sf.n);
    CHECK(fa_soundfont_open_memory(sf.d, sf.n, &font) == FA_OK);
    free(sf.d);
    note(&sg, 1000, 1500, 69);
    note(&sg, 127000, 128000, 69);
    cc(&sg, 160000, 7, 100);    /* the song's last event: it lasts 160 s */
    m = finish(&sg);
    fa_open_options_init(&o);
    o.flags = FA_OPEN_DECODE_ONLY;
    o.soundfont = font;
    CHECK(fa_stream_open_memory(g_engine, m.d, m.n, &o, &s) == FA_OK);
    CHECK(stream_silence(s, NULL, &edge) == FA_OK);
    CHECK(analysis_silence(s, FA_ANALYZE_NO_CACHE, &full) == FA_OK);
    fa_stream_close(s);
    fa_soundfont_close(font);
    free(m.d);
    printf("  first %.4f s, last %.4f s (full %.4f s), %u region(s)\n", (double)edge.info.first_audible / RATE,
           (double)edge.info.last_audible / RATE, (double)full.info.last_audible / RATE, edge.n);
    CHECK_EQ(edge.info.flags, 0);
    CHECK_EQ(edge.info.first_audible, full.info.first_audible);
    CHECK(edge.info.first_audible >= sec(1.0, RATE) && edge.info.first_audible < sec(1.01, RATE));
    CHECK(full.info.last_audible > sec(130.2, RATE));   /* the release rings past the window edge at 130 s */
    CHECK(llabs((long long)(edge.info.last_audible - full.info.last_audible)) <= RATE / 100);
    CHECK_EQ(full.n, 3);
    CHECK_EQ(edge.n, 2);
}

/* ---- analysis cache ------------------------------------------------------------------- */

static void remove_dir(const char* dir)
{
    char path[1024 + 300];
#if defined(_WIN32)
    struct _finddata_t fd;
    intptr_t h;
    snprintf(path, sizeof(path), "%s/*", dir);
    h = _findfirst(path, &fd);
    if (h != -1) {
        do {
            if (fd.attrib & _A_SUBDIR) continue;
            snprintf(path, sizeof(path), "%s/%s", dir, fd.name);
            remove(path);
        } while (_findnext(h, &fd) == 0);
        _findclose(h);
    }
    _rmdir(dir);
#else
    DIR* d = opendir(dir);
    struct dirent* e;
    if (d) {
        while ((e = readdir(d)) != NULL) {
            if (strcmp(e->d_name, ".") == 0 || strcmp(e->d_name, "..") == 0) continue;
            snprintf(path, sizeof(path), "%s/%s", dir, e->d_name);
            remove(path);
        }
        closedir(d);
    }
    rmdir(dir);
#endif
}

static void cache_checks(const char* dir)
{
    static const double spans[] = { 2.0, 50.0, 60.0, 95.0 };
    fa_engine_config cfg;
    fa_engine* e = NULL;
    fa_stream* s = NULL;
    fa_silence_options o;
    fa_analysis_options ao;
    fa_analysis* a = NULL;
    fa_frames n;
    float* x;
    buf w;
    result before, after, other;
    fa_engine_config_init(&cfg);
    cfg.flags |= FA_ENGINE_NULL_OUTPUT;
    cfg.cache_directory = dir;
    CHECK(fa_engine_create(&cfg, &e) == FA_OK);
    x = tones(110.0, LOW_RATE, spans, 2, &n);
    x[n - 1] = (float)(time(NULL) % 100000) * 1e-9f;   /* content this run's own */
    w = make_wav(x, n, LOW_RATE);
    free(x);
    s = open_mem(e, &w);
    CHECK(s);
    /* Nothing cached: the edge scan. */
    CHECK(stream_silence(s, NULL, &before) == FA_OK);
    CHECK_EQ(before.info.flags, 0);
    CHECK_EQ(before.n, 2);
    /* A library scan with SILENCE (and other kinds), then every region from the cache. */
    fa_analysis_options_init(&ao);
    ao.kinds = FA_ANALYZE_SILENCE | FA_ANALYZE_WAVEFORM;
    CHECK(fa_analyze_stream(s, &ao, &a) == FA_OK);
    fa_analysis_free(a);
    a = NULL;
    CHECK(stream_silence(s, NULL, &after) == FA_OK);
    CHECK_EQ(after.info.flags, FA_SILENCE_COMPLETE | FA_SILENCE_FROM_CACHE);
    CHECK_EQ(after.n, 3);
    CHECK_EQ(after.info.first_audible, before.info.first_audible);
    CHECK_EQ(after.info.last_audible, before.info.last_audible);
    CHECK_EQ(after.info.length, n);
    /* The analysis reads its own result back too. */
    ao.kinds = FA_ANALYZE_SILENCE;
    CHECK(fa_analyze_stream(s, &ao, &a) == FA_OK);
    {
        fa_analysis_info info;
        result c;
        info.struct_size = sizeof(info);
        CHECK(fa_analysis_get_info(a, &info) == FA_OK && (info.flags & FA_ANALYSIS_FROM_CACHE));
        memset(&c, 0, sizeof(c));
        c.info.struct_size = sizeof(c.info);
        CHECK(fa_analysis_get_silence(a, &c.info, c.regs, MAX_REGIONS, &c.n) == FA_OK);
        CHECK(c.n == after.n && memcmp(c.regs, after.regs, c.n * sizeof(c.regs[0])) == 0);
        {
            fa_beat_grid g;
            memset(&g, 0, sizeof(g));
            g.struct_size = sizeof(g);
            CHECK(fa_analysis_get_beat_grid(a, &g) == FA_ERROR_NOT_FOUND);
        }
    }
    fa_analysis_free(a);
    a = NULL;
    /* Other settings are not in the cache: the edge scan again. */
    fa_silence_options_init(&o);
    o.min_length_ms = 500;
    CHECK(stream_silence(s, &o, &other) == FA_OK);
    CHECK_EQ(other.info.flags, 0);
    /* A request for SILENCE at other settings measures again and rewrites the file. */
    ao.silence_min_length_ms = 500;
    CHECK(fa_analyze_stream(s, &ao, &a) == FA_OK);
    {
        fa_analysis_info info;
        info.struct_size = sizeof(info);
        CHECK(fa_analysis_get_info(a, &info) == FA_OK && !(info.flags & FA_ANALYSIS_FROM_CACHE));
        CHECK(info.kinds == (FA_ANALYZE_SILENCE | FA_ANALYZE_WAVEFORM));
    }
    fa_analysis_free(a);
    CHECK(stream_silence(s, &o, &other) == FA_OK);
    CHECK_EQ(other.info.flags, FA_SILENCE_COMPLETE | FA_SILENCE_FROM_CACHE);
    /* A new kind with default options measures the cached kinds again: silence keeps
     * its own settings (500 ms), not the request's defaults. */
    fa_analysis_options_init(&ao);
    ao.kinds = FA_ANALYZE_LOUDNESS;
    CHECK(fa_analyze_stream(s, &ao, &a) == FA_OK);
    fa_analysis_free(a);
    CHECK(stream_silence(s, &o, &other) == FA_OK);
    CHECK_EQ(other.info.flags, FA_SILENCE_COMPLETE | FA_SILENCE_FROM_CACHE);
    /* A truncated output struct is filled up to its size. */
    {
        fa_silence small;
        memset(&small, 0xAB, sizeof(small));
        small.struct_size = (uint32_t)offsetof(fa_silence, first_audible);
        CHECK(fa_stream_get_silence(s, &o, &small, NULL, 0, NULL) == FA_OK);
        CHECK_EQ(small.struct_size, offsetof(fa_silence, first_audible));
        CHECK_EQ(small.sample_rate, LOW_RATE);
        CHECK_EQ((uint8_t)(small.first_audible & 0xFF), 0xAB);
    }
    fa_stream_close(s);
    free(w.d);
    fa_engine_destroy(e);
}

static void test_cache(void)
{
    char dir[1024];
    const char* tmp = getenv("TMPDIR");
    if (!tmp || !*tmp) tmp = getenv("TEMP");
    if (!tmp || !*tmp) tmp = getenv("TMP");
#if defined(_WIN32)
    if (!tmp || !*tmp) tmp = ".";
#elif defined(__ANDROID__)
    if (!tmp || !*tmp) tmp = "/data/local/tmp";   /* Android has no /tmp */
#else
    if (!tmp || !*tmp) tmp = "/tmp";
#endif
    snprintf(dir, sizeof(dir), "%s/fa_silence_cache_%ld_%ld", tmp, (long)time(NULL), (long)clock());
    cache_checks(dir);
    remove_dir(dir);
}

int main(void)
{
    fa_engine_config cfg;
    buf sf = make_sf2(NULL, 0, NULL, 0);
    fa_engine_config_init(&cfg);
    cfg.flags = FA_ENGINE_NULL_OUTPUT;
    cfg.worker_threads = 1;
    if (fa_engine_create(&cfg, &g_engine) != FA_OK) { fprintf(stderr, "engine create failed\n"); return 1; }
    if (fa_soundfont_open_memory(sf.d, sf.n, &g_font) != FA_OK) { fprintf(stderr, "test soundfont rejected\n"); return 1; }
    free(sf.d);
    RUN(test_detector_rule);
    RUN(test_detector_threshold);
    RUN(test_args);
    RUN(test_short_file);
    RUN(test_tail_in_first_window);
    RUN(test_tail_in_later_window);
    RUN(test_long_trailing_silence);
    RUN(test_all_silent);
    RUN(test_not_seekable);
    RUN(test_midi_tail);
    RUN(test_cache);
    fa_soundfont_close(g_font);
    fa_engine_destroy(g_engine);
    if (g_failures) { fprintf(stderr, "%d failure(s)\n", g_failures); return 1; }
    printf("all passed\n");
    return 0;
}
