/* Render-ahead against decoding: two playing streams with tempo on, each with 2160p
 * surfaces at full effects drawn 60 times a second, on the null output, for a while.
 * Reports per stream the dropouts (fa_stream_stats.underruns) and how far the clock
 * fell behind wall time, and per surface how many frames came back behind. See
 * tools/render-stress/README.md. Not a CI test: it measures, it does not check. */
#ifndef _WIN32
#define _POSIX_C_SOURCE 200809L
#endif
#include <fatzaudio/fatzaudio.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

#ifdef _WIN32
#include <windows.h>
typedef HANDLE thr_t;
static int64_t now_ns(void)
{
    LARGE_INTEGER f, c;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&c);
    return (int64_t)((double)c.QuadPart * 1e9 / (double)f.QuadPart);
}
static void sleep_ms(int ms) { Sleep((DWORD)ms); }
#else
#include <pthread.h>
#include <time.h>
typedef pthread_t thr_t;
static int64_t now_ns(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (int64_t)ts.tv_sec * 1000000000 + ts.tv_nsec;
}
static void sleep_ms(int ms)
{
    struct timespec ts;
    ts.tv_sec = ms / 1000;
    ts.tv_nsec = (long)(ms % 1000) * 1000000L;
    nanosleep(&ts, NULL);
}
#endif

typedef struct { uint8_t* p; size_t n, cap; } buf;

static void put(buf* b, const void* d, size_t n)
{
    if (b->n + n > b->cap) {
        b->cap = (b->n + n) * 2;
        b->p = (uint8_t*)realloc(b->p, b->cap);
        if (!b->p) { fprintf(stderr, "out of memory\n"); exit(1); }
    }
    memcpy(b->p + b->n, d, n);
    b->n += n;
}
static void put16(buf* b, unsigned v) { uint8_t x[2] = { (uint8_t)v, (uint8_t)(v >> 8) }; put(b, x, 2); }
static void put32(buf* b, uint32_t v) { uint8_t x[4] = { (uint8_t)v, (uint8_t)(v >> 8), (uint8_t)(v >> 16), (uint8_t)(v >> 24) }; put(b, x, 4); }

/* A sine with a slow wobble, stereo 16-bit, 44.1 kHz. */
static buf make_wav(int seconds, double hz)
{
    buf b = { 0 };
    uint32_t frames = (uint32_t)seconds * 44100u, i;
    put(&b, "RIFF", 4); put32(&b, 36 + frames * 4); put(&b, "WAVEfmt ", 8);
    put32(&b, 16); put16(&b, 1); put16(&b, 2); put32(&b, 44100); put32(&b, 44100 * 4); put16(&b, 4); put16(&b, 16);
    put(&b, "data", 4); put32(&b, frames * 4);
    for (i = 0; i < frames; i++) {
        double t = (double)i / 44100.0;
        int v = (int)(8000.0 * sin(2.0 * 3.14159265358979 * hz * t) * (0.6 + 0.4 * sin(t)));
        put16(&b, (unsigned)(uint16_t)(int16_t)v);
        put16(&b, (unsigned)(uint16_t)(int16_t)v);
    }
    return b;
}

static void cdg_pack(buf* b, int instruction, const uint8_t data[16])
{
    uint8_t p[24] = { 0 };
    p[0] = 9;
    p[1] = (uint8_t)instruction;
    memcpy(p + 4, data, 16);
    put(b, p, sizeof(p));
}

/* Graphics like a karaoke song's: a tile every 4 packs (lyrics drawn and coloured in,
 * 75 a second), new colours every `colour_ms` and a clear every 20 s (full redraws). */
static buf make_cdg(int seconds, int colour_ms)
{
    size_t every = (size_t)colour_ms * 300 / 1000;
    buf b = { 0 };
    size_t packs = (size_t)seconds * 300, n;
    uint8_t d[16];
    int i;
    for (n = 0; n < packs; n++) {
        memset(d, 0, sizeof(d));
        if (n % 6000 == 0) {                 /* clear to colour 0 */
            cdg_pack(&b, 1, d);
        } else if (n % every == 1) {         /* load colours 0-7 */
            for (i = 0; i < 8; i++) {
                int r = (int)((n / every + (size_t)i * 2) % 16), g = (int)((n / every * 3 + (size_t)i) % 16), bl = 15 - r;
                d[2 * i] = (uint8_t)((r << 2) | (g >> 2));
                d[2 * i + 1] = (uint8_t)(((g & 3) << 4) | bl);
            }
            cdg_pack(&b, 30, d);
        } else if (n % 4 == 0) {             /* a tile along a line of text */
            size_t k = n / 4;
            d[0] = 0;
            d[1] = (uint8_t)(1 + k % 7);
            d[2] = (uint8_t)(3 + (k / 48) % 12);
            d[3] = (uint8_t)(1 + k % 48);
            for (i = 0; i < 12; i++) d[4 + i] = (uint8_t)(((k * 37 + (size_t)i * 11) & 0x3F) | 0x0C);
            cdg_pack(&b, (k / 48) % 2 ? 38 : 6, d);
        } else {
            uint8_t z[24] = { 0 };
            put(&b, z, sizeof(z));
        }
    }
    return b;
}

static void put_var(buf* b, uint32_t v)
{
    uint8_t x[5];
    int n = 0, i;
    x[n++] = (uint8_t)(v & 0x7F);
    while (v >>= 7) x[n++] = (uint8_t)(0x80 | (v & 0x7F));
    for (i = n - 1; i >= 0; i--) put(b, &x[i], 1);
}

/* A format 0 MIDI file: a note every beat and a lyric syllable every 240 ms, a new
 * line every 8. 480 ticks a beat at 120 bpm: 1 tick = 1.04 ms. */
static buf make_midi(int seconds)
{
    static const char* syl[] = { "Sing ", "a ", "long ", "with ", "the ", "lyr", "ics ", "now " };
    buf trk = { 0 }, b = { 0 };
    int i, count = seconds * 1000 / 240;
    uint8_t ev[4];
    for (i = 0; i < count; i++) {
        char text[32];
        size_t len;
        snprintf(text, sizeof(text), "%s%s", i % 8 == 0 ? "\r" : "", syl[i % 8]);
        len = strlen(text);
        put_var(&trk, i ? 230 : 0);
        ev[0] = 0xFF; ev[1] = 0x05; put(&trk, ev, 2); put_var(&trk, (uint32_t)len); put(&trk, text, len);
        put_var(&trk, 0);
        ev[0] = 0x90; ev[1] = (uint8_t)(60 + i % 12); ev[2] = 90; put(&trk, ev, 3);
    }
    put_var(&trk, 230);
    ev[0] = 0xFF; ev[1] = 0x2F; ev[2] = 0; put(&trk, ev, 3);
    put(&b, "MThd", 4);
    ev[0] = 0; ev[1] = 0; ev[2] = 0; ev[3] = 6; put(&b, ev, 4);
    ev[0] = 0; ev[1] = 0; ev[2] = 0; ev[3] = 1; put(&b, ev, 4);
    ev[0] = 0x01; ev[1] = 0xE0; put(&b, ev, 2);      /* 480 ticks per beat */
    put(&b, "MTrk", 4);
    ev[0] = (uint8_t)(trk.n >> 24); ev[1] = (uint8_t)(trk.n >> 16); ev[2] = (uint8_t)(trk.n >> 8); ev[3] = (uint8_t)trk.n;
    put(&b, ev, 4);
    put(&b, trk.p, trk.n);
    free(trk.p);
    return b;
}

#define MAX_SURF 12
#define MAX_STREAMS 2

typedef struct {
    fa_surface* s;
    int stream;
    fa_surface_layout lay;
    uint8_t* mem[2];
    uint64_t rev[2];
    uint32_t frames, behind, slow;   /* slow: update_into over 8 ms */
    double max_ms, total_ms;
    const char* what;
} surf;

static surf g_surf[MAX_SURF];
static int g_nsurf;
static volatile int g_stop;

/* arg: NULL draws every surface, else the surface it points to (--threads). */
static void* draw_main(void* arg)
{
    int64_t next = now_ns();
    uint32_t b = 0;
    int first = arg ? (int)((surf*)arg - g_surf) : 0, last = arg ? first + 1 : g_nsurf;
    while (!g_stop) {
        int i;
        for (i = first; i < last; i++) {
            surf* u = &g_surf[i];
            fa_surface_frame fr;
            int64_t t0;
            double ms;
            memset(&fr, 0, sizeof(fr));
            fr.struct_size = sizeof(fr);
            t0 = now_ns();
            if (fa_surface_update_into(u->s, FA_SURFACE_NOW, u->mem[b], (int32_t)(u->lay.width * 4), u->lay.width,
                                       u->lay.height, u->rev[b], &fr) != FA_OK)
                continue;
            ms = (double)(now_ns() - t0) / 1e6;
            u->rev[b] = fr.revision;
            u->frames++;
            if (fr.behind) u->behind++;
            if (ms > 8.0) u->slow++;
            if (ms > u->max_ms) u->max_ms = ms;
            u->total_ms += ms;
        }
        b ^= 1;
        next += 16666667;
        {
            int64_t wait = next - now_ns();
            if (wait > 0) sleep_ms((int)(wait / 1000000));
            else next = now_ns();
        }
    }
    return NULL;
}

static void* burn_main(void* arg)
{
    volatile uint64_t x = 0;
    (void)arg;
    while (!g_stop) x++;
    return NULL;
}

#ifdef _WIN32
static DWORD WINAPI draw_w(LPVOID a) { draw_main(a); return 0; }
static DWORD WINAPI burn_w(LPVOID a) { burn_main(a); return 0; }
static void start(thr_t* t, int draw, void* arg) { *t = CreateThread(NULL, 0, draw ? draw_w : burn_w, arg, 0, NULL); }
static void join(thr_t t) { WaitForSingleObject(t, INFINITE); CloseHandle(t); }
#else
static void start(thr_t* t, int draw, void* arg) { pthread_create(t, NULL, draw ? draw_main : burn_main, arg); }
static void join(thr_t t) { pthread_join(t, NULL); }
#endif

static void check(fa_result r, const char* what)
{
    if (r != FA_OK) {
        fprintf(stderr, "%s: %s\n", what, fa_result_string(r));
        exit(1);
    }
}

static void add_surface(fa_surface* s, int stream, int render_ahead, uint32_t w, uint32_t h, const char* what)
{
    surf* u;
    if (g_nsurf >= MAX_SURF) { fprintf(stderr, "too many surfaces\n"); exit(1); }
    u = &g_surf[g_nsurf++];
    fa_surface_effects fx;
    u->s = s;
    u->stream = stream;
    u->what = what;
    fa_surface_effects_init(&fx);
    fx.shadow.on = 1; fx.shadow.size = 1.0f; fx.shadow.distance = 1.0f; fx.shadow.fade = 0.5f;
    fx.outline.on = 1; fx.outline.size = 1.0f; fx.outline.fade = 0.3f;
    fx.bevel.on = 1; fx.bevel.size = 1.0f; fx.bevel.strength = 1.0f;
    check(fa_surface_set_effects(s, &fx), "effects");
    check(fa_surface_set_presentation_size(s, w, h), "presentation size");
    check(fa_surface_set_render_ahead(s, render_ahead), "render-ahead");
    memset(&u->lay, 0, sizeof(u->lay));
    u->lay.struct_size = sizeof(u->lay);
    check(fa_surface_get_layout(s, &u->lay), "layout");
    u->mem[0] = (uint8_t*)calloc((size_t)u->lay.width * u->lay.height, 4);
    u->mem[1] = (uint8_t*)calloc((size_t)u->lay.width * u->lay.height, 4);
    if (!u->mem[0] || !u->mem[1]) { fprintf(stderr, "out of memory\n"); exit(1); }
}

static void add_cdg(fa_stream* st, int stream, int render_ahead, uint32_t w, uint32_t h, int smooth)
{
    fa_cdg_surface* f;
    fa_cdg_presentation p;
    check(fa_cdg_surface_create(st, NULL, &f), "cdg surface");
    fa_cdg_presentation_init(&p);
    p.scale = 1;
    p.smoothness = smooth;
    p.edge_blend = smooth < 5;
    check(fa_cdg_surface_set_presentation(f, &p), "presentation");
    add_surface(f, stream, render_ahead, w, h, smooth >= 5 ? "cd+g traced" : "cd+g blend");
}

static void usage(void)
{
    printf("fa_render_stress [--seconds N] [--render-ahead 0|1] [--workers N] [--burn N]\n"
           "                 [--size WxH] [--tempo T] [--soundfont file.sf2]\n"
           "                 [--extra N] [--latency ms] [--colours ms] [--lyrics N] [--threads]\n"
           "  Two streams with tempo T (default 1.05 and 0.95), two CD+G surfaces each\n"
           "  (traced and blended, every effect), drawn at 60 Hz on the null output.\n"
           "  With a soundfont, the second stream is MIDI with lyrics and has a lyric\n"
           "  surface (--lyrics N: N of them, presets in turn) in place of its second\n"
           "  CD+G surface. --burn adds busy threads\n"
           "  standing in for the app's own work. --extra adds N more CD+G surfaces per\n"
           "  stream; --threads draws each surface on a thread of its own; --latency sets the tempo latency (default 150 ms); --colours how\n"
           "  often the CD+G loads new colours, a full redraw (default 10000 ms). Run under\n"
           "  taskset to pin the cores.\n");
}

int main(int argc, char** argv)
{
    int seconds = 30, ra = 1, burn = 0, extra = 0, latency = 0, colour_ms = 10000, lyrics = 1, i, k;
    uint32_t workers = 0, w = 3840, h = 2160;
    double tempo[MAX_STREAMS] = { 1.05, 0.95 };
    const char* sf_path = NULL;
    fa_engine_config cfg;
    fa_engine* e;
    fa_device* dev;
    fa_stream* st[MAX_STREAMS];
    fa_soundfont* sf = NULL;
    buf wav, cdg;
    thr_t draw[MAX_SURF], burners[16];
    int per_surface = 0, ndraw;
    int64_t wall0[MAX_STREAMS], t_end;
    fa_time clock0[MAX_STREAMS];
    double max_lag[MAX_STREAMS] = { 0, 0 }, last_lag[MAX_STREAMS] = { 0, 0 };
    int moving[MAX_STREAMS] = { 0, 0 };
    fa_time min_buf[MAX_STREAMS] = { INT64_MAX, INT64_MAX };

    for (i = 1; i < argc; i++) {
        if (!strcmp(argv[i], "--seconds") && i + 1 < argc) seconds = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--render-ahead") && i + 1 < argc) ra = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--workers") && i + 1 < argc) workers = (uint32_t)atoi(argv[++i]);
        else if (!strcmp(argv[i], "--burn") && i + 1 < argc) burn = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--size") && i + 1 < argc) { if (sscanf(argv[++i], "%ux%u", &w, &h) != 2) { usage(); return 2; } }
        else if (!strcmp(argv[i], "--tempo") && i + 1 < argc) tempo[0] = tempo[1] = atof(argv[++i]);
        else if (!strcmp(argv[i], "--soundfont") && i + 1 < argc) sf_path = argv[++i];
        else if (!strcmp(argv[i], "--extra") && i + 1 < argc) extra = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--latency") && i + 1 < argc) latency = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--colours") && i + 1 < argc) colour_ms = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--lyrics") && i + 1 < argc) lyrics = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--threads")) per_surface = 1;
        else { usage(); return 2; }
    }
    if (burn > 16) burn = 16;
    if (colour_ms < 10) colour_ms = 10;

    fa_engine_config_init(&cfg);
    cfg.flags = FA_ENGINE_NULL_OUTPUT;
    cfg.worker_threads = workers;
    check(fa_engine_create(&cfg, &e), "engine");
    check(fa_device_open(e, NULL, &dev), "device");
    check(fa_device_start(dev), "device start");
    if (sf_path) {
        check(fa_soundfont_open_path(sf_path, &sf), "soundfont");
        fa_engine_set_soundfont(e, sf);
    }

    wav = make_wav(seconds + 15, 330.0);
    cdg = make_cdg(seconds + 15, colour_ms);
    for (i = 0; i < MAX_STREAMS; i++) {
        if (i == 1 && sf) {
            buf mid = make_midi(seconds + 15);
            check(fa_stream_open_memory(e, mid.p, mid.n, NULL, &st[i]), "midi stream");
            free(mid.p);
        } else {
            check(fa_stream_open_memory(e, wav.p, wav.n, NULL, &st[i]), "stream");
        }
        check(fa_stream_load_cdg_memory(st[i], cdg.p, cdg.n), "cd+g");
        check(fa_stream_attach(st[i], dev, NULL), "attach");
        check(fa_stream_set_param(st[i], FA_PARAM_TEMPO, (float)tempo[i]), "tempo");
        if (latency) {
            fa_tempo_options to;
            fa_tempo_options_init(&to);
            to.latency_ms = (uint32_t)latency;
            check(fa_stream_set_tempo_options(st[i], &to), "tempo latency");
        }
        /* Room left for this stream's two surfaces and the later streams' two each. */
        for (k = 0; k < extra && g_nsurf + 2 * (MAX_STREAMS - i) < MAX_SURF; k++) add_cdg(st[i], i, ra, w, h, k % 2 ? 2 : 9);
        add_cdg(st[i], i, ra, w, h, 9);
        if (i == 1 && sf) {
            for (k = 0; k < lyrics && g_nsurf < MAX_SURF; k++) {
                fa_surface* ly;
                fa_lyric_style style;
                fa_lyric_style_init(&style, (fa_lyric_preset)(k % 3));
                check(fa_lyric_surface_create(st[i], &style, &ly), "lyric surface");
                add_surface(ly, i, ra, w, h, "lyrics");
            }
        } else {
            add_cdg(st[i], i, ra, w, h, 2);
        }
    }
    free(wav.p);
    free(cdg.p);

    {
        fa_engine_stats es;
        memset(&es, 0, sizeof(es));
        es.struct_size = sizeof(es);
        fa_engine_get_stats(e, &es);
        printf("render-ahead %s, %u workers, %d burners, %ux%u, %d s\n", ra ? "on" : "off", es.workers, burn, w, h, seconds);
    }
    for (i = 0; i < burn; i++) start(&burners[i], 0, NULL);
    for (i = 0; i < MAX_STREAMS; i++) check(fa_stream_play(st[i]), "play");
    ndraw = per_surface ? g_nsurf : 1;
    for (i = 0; i < ndraw; i++) start(&draw[i], 1, per_surface ? (void*)&g_surf[i] : NULL);

    /* The clock should move at the tempo against wall time: lag is how far it fell
     * behind that since it first moved. */
    t_end = now_ns() + (int64_t)seconds * 1000000000;
    while (now_ns() < t_end) {
        for (i = 0; i < MAX_STREAMS; i++) {
            fa_time c;
            fa_stream_stats ss;
            int64_t now = now_ns();
            memset(&ss, 0, sizeof(ss));
            ss.struct_size = sizeof(ss);
            if (fa_stream_get_clock(st[i], &c) != FA_OK) continue;
            if (fa_stream_get_stats(st[i], &ss) == FA_OK && moving[i] && ss.buffered < min_buf[i]) min_buf[i] = ss.buffered;
            if (!moving[i]) {
                if (c > 0) { moving[i] = 1; wall0[i] = now; clock0[i] = c; }
                continue;
            }
            last_lag[i] = (double)(now - wall0[i]) * tempo[i] / 1e6 - (double)(c - clock0[i]) / 1e6;
            if (last_lag[i] > max_lag[i]) max_lag[i] = last_lag[i];
        }
        sleep_ms(5);
    }
    g_stop = 1;
    for (i = 0; i < ndraw; i++) join(draw[i]);
    for (i = 0; i < burn; i++) join(burners[i]);

    for (i = 0; i < MAX_STREAMS; i++) {
        fa_stream_stats ss;
        memset(&ss, 0, sizeof(ss));
        ss.struct_size = sizeof(ss);
        fa_stream_get_stats(st[i], &ss);
        printf("stream %d (tempo %.2f): underruns %u, clock lag max %.1f ms (end %.1f ms), least buffered %.1f ms\n", i,
               tempo[i], ss.underruns, max_lag[i], last_lag[i], min_buf[i] == INT64_MAX ? 0.0 : (double)min_buf[i] / 1e6);
    }
    for (i = 0; i < g_nsurf; i++) {
        surf* u = &g_surf[i];
        printf("surface %d (stream %d, %s, %ux%u): %u frames, %u behind (%.0f%%), %u over 8 ms, update_into avg %.2f ms max %.1f ms\n",
               i, u->stream, u->what, u->lay.width, u->lay.height, u->frames, u->behind,
               u->frames ? 100.0 * u->behind / u->frames : 0.0, u->slow, u->frames ? u->total_ms / u->frames : 0.0, u->max_ms);
    }
    {
        fa_engine_stats es;
        memset(&es, 0, sizeof(es));
        es.struct_size = sizeof(es);
        fa_engine_get_stats(e, &es);
        printf("engine: underruns %u, worker load %.0f%%\n", es.underruns, es.worker_load);
    }
    for (i = 0; i < g_nsurf; i++) {
        fa_surface_destroy(g_surf[i].s);
        free(g_surf[i].mem[0]);
        free(g_surf[i].mem[1]);
    }
    for (i = 0; i < MAX_STREAMS; i++) fa_stream_close(st[i]);
    fa_device_close(dev);
    fa_engine_destroy(e);
    if (sf) fa_soundfont_close(sf);
    return 0;
}
