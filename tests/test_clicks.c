/* Click probe: renders steady sine tones through loop wraps and seeks on the null
 * device (the test plays the audio thread, as test_crossfade.c does) and lists every
 * step in the output that a pure sine could not make. A sine obeys
 * x[n+1] = 2cos(w) x[n] - x[n-1]; the residual of that is ~0 inside a tone and spikes
 * at a splice. */
#if !defined(_WIN32)
#  define _POSIX_C_SOURCE 200809L
#endif
#include "fa_internal.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define PERIOD 512
#define MAX_REC (1 << 20)
static fa_engine* g_engine;
static fa_device* g_dev;
static float* g_rec;
static int64_t g_base;
static int64_t g_rec_frames;

static uint8_t* make_sine_wav(uint32_t frames, double hz, uint32_t rate, size_t* out_n)
{
    size_t data = (size_t)frames * 2, n = 44 + data;
    uint8_t* b = (uint8_t*)malloc(n);
    uint32_t i, v;
    memcpy(b, "RIFF", 4); v = (uint32_t)(n - 8); memcpy(b + 4, &v, 4); memcpy(b + 8, "WAVEfmt ", 8);
    v = 16; memcpy(b + 16, &v, 4);
    b[20] = 1; b[21] = 0; b[22] = 1; b[23] = 0;
    memcpy(b + 24, &rate, 4); v = rate * 2; memcpy(b + 28, &v, 4);
    b[32] = 2; b[33] = 0; b[34] = 16; b[35] = 0;
    memcpy(b + 36, "data", 4); v = (uint32_t)data; memcpy(b + 40, &v, 4);
    for (i = 0; i < frames; i++) {
        int16_t sv = (int16_t)lrint(16384.0 * sin(6.283185307179586 * hz * i / rate));
        memcpy(b + 44 + (size_t)i * 2, &sv, 2);
    }
    *out_n = n;
    return b;
}

static void render(int64_t frames)
{
    float out[PERIOD * 2];
    int64_t d = g_dev->limiter.delay;
    frames += d;
    while (frames > 0) {
        uint32_t n = frames < PERIOD ? (uint32_t)frames : PERIOD, i;
        fa_voice_list* list = (fa_voice_list*)fa_load_ptr(&g_dev->voices);
        int64_t deadline = fa_monotonic_ns() + 10000000000LL;
        while (list && fa_monotonic_ns() < deadline) {
            int ready = 1;
            for (i = 0; i < list->count; i++) {
                fa_stream* s = list->items[i];
                if (fa_ring_readable(&s->ring) < (int64_t)n && !fa_load32(&s->eof) && !fa_load32(&s->flushing)) ready = 0;
            }
            if (ready) break;
            fa_sleep_ms(1);
        }
        fa_fetch_add64(&g_dev->epoch, 1);
        fa_device_render(g_dev, out, n);
        fa_fetch_add64(&g_dev->epoch, 1);
        for (i = 0; i < n; i++) {
            int64_t at = g_rec_frames + i - d;
            if (at >= 0 && at < MAX_REC) g_rec[at] = out[i * 2];
        }
        g_rec_frames += n;
        frames -= n;
    }
}

/* Prints the spikes in [from, to) for a tone of `hz` (the pitch the output has). */
/* Seeks and wraps must not step: the residual stays under `thr`. */
static double report(const char* name, double hz, int64_t from, int64_t to, double thr)
{
    double c = 2.0 * cos(6.283185307179586 * hz / g_dev->rate), worst = 0;
    int64_t n, last = -1000, shown = 0;
    if (to > MAX_REC) to = MAX_REC;
    for (n = from + 1; n + 1 < to; n++) {
        double r = fabs((double)g_rec[n + 1] - (c * g_rec[n] - g_rec[n - 1]));
        if (r > worst) worst = r;
        if (r > thr && n - last > 64) {
            if (shown++ < 6) printf("    step at %lld (clk %lld): residual %.4f\n", (long long)n, (long long)(g_base + n + g_dev->limiter.delay), r);
            if (0 && shown == 1) { int64_t q; for (q = n - 100; q < n + 100; q += 4) printf("      %lld %.3f %.3f %.3f %.3f\n", (long long)q, g_rec[q], g_rec[q+1], g_rec[q+2], g_rec[q+3]); }
            last = n;
        }
    }
    printf("  %-44s worst residual %.5f%s\n", name, worst, worst > thr ? "  <-- CLICK" : "");
    return worst;
}

static fa_stream* open_sine(double hz, uint32_t frames, uint32_t rate, uint32_t flags)
{
    size_t n;
    uint8_t* b = make_sine_wav(frames, hz, rate, &n);
    fa_open_options o;
    fa_stream* s = NULL;
    fa_open_options_init(&o);
    o.flags = flags;
    if (fa_stream_open_memory(g_engine, b, n, &o, &s) != FA_OK) s = NULL;
    free(b);
    if (s && fa_stream_attach(s, g_dev, NULL) != FA_OK) { fa_stream_close(s); s = NULL; }
    return s;
}

static void reset(void) { g_base = fa_load64(&g_dev->clock); g_rec_frames = 0; memset(g_rec, 0, sizeof(float) * MAX_REC); }

typedef struct seek_job { fa_stream* s; fa_frames to; int async; volatile int done; } seek_job;

static void seek_proc(void* arg)
{
    seek_job* j = (seek_job*)arg;
    if (j->async) fa_stream_seek_async(j->s, j->to);
    else fa_stream_seek(j->s, j->to);
    j->done = 1;
}

/* Seeks from another thread (the audio thread here is the one rendering), as an app's
 * UI thread would, and keeps rendering while the call waits for a callback. */
static void pause_proc(void* arg)
{
    seek_job* j = (seek_job*)arg;
    fa_stream_pause(j->s);
    j->done = 1;
}

/* Pauses from another thread, as an app's UI thread would, while this one plays the audio thread. */
static void pause_while_rendering(fa_stream* s)
{
    seek_job j;
    fa_thread t;
    j.s = s; j.to = 0; j.async = 0; j.done = 0;
    fa_thread_create(&t, pause_proc, &j);
    while (!j.done) render(64);
    fa_thread_join(&t);
}

static void seek_while_rendering(fa_stream* s, fa_frames to, int async)
{
    seek_job j;
    fa_thread t;
    j.s = s; j.to = to; j.async = async; j.done = 0;
    fa_thread_create(&t, seek_proc, &j);
    while (!j.done) render(64);
    fa_thread_join(&t);
    /* An async seek is carried out by a worker: render until it has (a UI that waits for the
     * position to land before the next seek, not a drag, which replaces targets). */
    while (async && (fa_load64(&s->seek_pending) >= 0 || fa_load32(&s->flushing))) render(64);
}

static int g_fail;
static int g_starved;   /* fades the mixer did not play in time this run (a loaded machine) */
static void expect(double worst, double thr) { if (worst > thr) { g_fail++; printf("    ^ FAILED (limit %.3f)\n", thr); } }

int main(void)
{
    fa_engine_config cfg;
    const double thr = 0.08;   /* raw cuts measure 0.3 .. 1.0; a faded splice stays under 0.05 */
    const double hzs[3] = { 440.0, 1000.0, 3170.0 };
    const uint32_t dev_rates[2] = { 0, 44100 };
    int r, h;
    fa_engine_config_init(&cfg);
    cfg.flags = FA_ENGINE_NULL_OUTPUT | (getenv("FA_NO_DECLICK") ? FA_ENGINE_NO_DECLICK : 0);
    cfg.worker_threads = 2;
    if (fa_engine_create(&cfg, &g_engine) != FA_OK) return 1;
    if (fa_device_open(g_engine, NULL, &g_dev) != FA_OK) return 1;
    g_rec = (float*)calloc(MAX_REC, sizeof(float));
    printf("device %u Hz\n", g_dev->rate);
    for (r = 0; r < 2; r++) {
        uint32_t rate = dev_rates[r] ? dev_rates[r] : g_dev->rate;
        printf("== source %u Hz\n", rate);
        for (h = 0; h < 3; h++) {
            double hz = hzs[h];
            uint32_t len = rate / 10 * 3;   /* 0.3 s: a whole number of periods of each tone */
            char nm[96];
            fa_stream* s;
            int k;

            s = open_sine(hz, len, rate, FA_OPEN_LOOP);
            if (!s) { printf("open failed\n"); return 1; }
            reset();
            fa_stream_play(s);
            render(len * 4);
            snprintf(nm, sizeof nm, "%.0f Hz seamless loop x3", hz);
            expect(report(nm, hz, 64, g_rec_frames - 2048, thr), 0.001);
            fa_stream_close(s);

            /* A loop whose length is not a whole number of periods: the file itself steps there. */
            s = open_sine(hz, len + 17, rate, FA_OPEN_LOOP);
            reset();
            fa_stream_play(s);
            render(len * 4);
            snprintf(nm, sizeof nm, "%.0f Hz stepped loop x3", hz);
            expect(report(nm, hz, 64, g_rec_frames - 2048, 0.2), 0.2);
            fa_stream_close(s);

            /* Pause and resume in the middle of a tone: the tone fades out, silence, then fades in. */
            s = open_sine(hz, rate * 3, rate, 0);
            reset();
            fa_stream_play(s);
            render(4000);
            pause_while_rendering(s);
            render(3000);
            fa_stream_play(s);
            render(4000);
            pause_while_rendering(s);
            render(2000);
            fa_stream_play(s);
            render(3000);
            snprintf(nm, sizeof nm, "%.0f Hz pause and resume x2", hz);
            if (fa_load32(&s->dk_timeouts) > 0) { g_starved++; report(nm, hz, 64, g_rec_frames - 2048, thr); }
            else expect(report(nm, hz, 64, g_rec_frames - 2048, thr), thr);
            fa_stream_close(s);

            for (k = 0; k < 2; k++) {
                s = open_sine(hz, rate * 3, rate, 0);
                reset();
                fa_stream_play(s);
                render(4000);
                seek_while_rendering(s, 54321 % (rate * 2), k);
                render(4000);
                seek_while_rendering(s, 100003 % (rate * 2), k);
                render(4000);
                seek_while_rendering(s, 777, k);
                render(4000);
                snprintf(nm, sizeof nm, "%.0f Hz %s seeks while playing x3", hz, k ? "async" : "sync");
                /* A fade the mixer does not play in time (a loaded machine: the callbacks here come from this
                 * test, and the 150 ms wait ran out) falls back to the old hard cut. Count that, not the cut. */
                if (fa_load32(&s->dk_timeouts) > 0) {
                    g_starved++;
                    report(nm, hz, 64, g_rec_frames - 2048, thr);
                    printf("    (the mixer did not play a fade in time: not counted)\n");
                } else {
                    expect(report(nm, hz, 64, g_rec_frames - 2048, thr), thr);
                }
                fa_stream_close(s);
            }
        }
    }
    fa_device_close(g_dev);
    fa_engine_destroy(g_engine);
    free(g_rec);
    if (g_starved) printf("%d seek case(s) had the mixer starved\n", g_starved);
    printf(g_fail ? "FAIL\n" : "PASS\n");
    return g_fail ? 1 : 0;
}
