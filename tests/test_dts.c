/* DTS through the system's decoder (src/dts.c): FatzAudio has no DTS decoder, so a
 * stand-in for the system's one (fa_test_system_codec) takes the frames. It checks
 * that every packet it gets is one whole big-endian frame and puts out, for each
 * frame, its length in samples of one value made from the frame's bytes. So the
 * tests see which frame came out where: lengths, positions and seeks of the raw
 * reader, the MPEG-PS/TS, AVI and Matroska paths, and the packet codec's framing.
 * The files in tests/data/dts come from make_dts_data.sh (FFmpeg's DTS encoder). */
#if !defined(_WIN32)
#  define _POSIX_C_SOURCE 200809L
#endif
#include "fa_internal.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifndef FA_TEST_DATA_DIR
#  define FA_TEST_DATA_DIR "data"
#endif

static int g_failures = 0;
#define CHECK(cond) do { if (!(cond)) { fprintf(stderr, "  FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); g_failures++; return; } } while (0)
#define CHECK_EQ(a, b) do { long long va_ = (long long)(a), vb_ = (long long)(b); if (va_ != vb_) { \
    fprintf(stderr, "  FAIL %s:%d: %s == %s (%lld vs %lld)\n", __FILE__, __LINE__, #a, #b, va_, vb_); g_failures++; return; } } while (0)
#define CHECK_STR(a, b) do { if (strcmp((a), (b)) != 0) { \
    fprintf(stderr, "  FAIL %s:%d: %s == \"%s\" (got \"%s\")\n", __FILE__, __LINE__, #a, (b), (a)); g_failures++; return; } } while (0)
#define RUN(t) do { int before_ = g_failures; printf("%s\n", #t); t(); if (g_failures == before_) printf("  ok\n"); } while (0)

static fa_engine* g_engine;

/* ---- the stand-in system decoder ------------------------------------------------- */

static int g_hd_ok = 1;          /* it takes DTS-HD ("dtshd"), else only "dts" */
static char g_opened[16];        /* the name it was opened with */
static int g_bad;                /* packets that were not one whole big-endian frame */
static size_t g_last_size;       /* bytes of the last packet */
static int64_t g_units;

static float frame_value(const uint8_t* d, size_t n)
{
    uint32_t h = 2166136261u;
    size_t i;
    for (i = 0; i < n; i++) h = (h ^ d[i]) * 16777619u;
    return (float)(h % 20001u) / 20000.0f - 0.5f;
}

static fa_result fake_decode(fa_codec* c, const uint8_t* d, size_t n)
{
    fa_dts_header h;
    size_t len = 0, i;
    float v;
    fa_result r;
    c->out_frames = 0;
    if (!d) return FA_OK;
    g_last_size = n;
    g_units++;
    if (n < 4 || d[0] != 0x7F || d[1] != 0xFE || fa_dts_unit(d, n, 1, &h, &len) != 1 ||
        (len != n && !(strcmp(g_opened, "dts") == 0 && n == h.size))) {
        g_bad++;
        return FA_OK;
    }
    v = frame_value(d, n);
    c->rate = h.rate;
    c->channels = h.channels;
    r = fa_codec_reserve(c, h.samples, h.channels);
    if (r != FA_OK) return r;
    for (i = 0; i < (size_t)h.samples * h.channels; i++) c->out[i] = v;
    c->out_frames = h.samples;
    return FA_OK;
}

static void fake_flush(fa_codec* c) { (void)c; }
static void fake_close(fa_codec* c) { (void)c; }
static const fa_codec_vtable fake_vtable = { "fake-system", fake_decode, fake_flush, fake_close };

static fa_result fake_open(fa_codec* c, const fa_codec_params* p)
{
    if (!(strcmp(p->codec, "dts") == 0 || (g_hd_ok && strcmp(p->codec, "dtshd") == 0))) return FA_ERROR_UNSUPPORTED_FORMAT;
    snprintf(g_opened, sizeof(g_opened), "%s", p->codec);
    c->vt = &fake_vtable;
    c->rate = p->sample_rate;
    c->channels = p->channels;
    return FA_OK;
}

static void use_fake(int hd_ok)
{
    fa_test_system_codec = fake_open;
    g_hd_ok = hd_ok;
    g_opened[0] = 0;
    g_bad = 0;
    g_units = 0;
}

/* ---- made-up streams ---------------------------------------------------------------- */

typedef struct buf { uint8_t* p; size_t n, cap; } buf;

static void put(buf* b, const void* d, size_t n)
{
    if (b->n + n > b->cap) {
        b->cap = (b->n + n) * 2 + 1024;
        b->p = (uint8_t*)realloc(b->p, b->cap);
    }
    memcpy(b->p + b->n, d, n);
    b->n += n;
}

#define CORE 1024                /* bytes of a made-up core frame: 512 samples of 48 kHz 5.1 */

/* Core frame k: the header (5.4.2), then k and filler. */
static void core_frame(uint8_t* f, int k)
{
    unsigned nblks = 15, fsize = CORE - 1, amode = 9, sfreq = 13, rate = 15;
    size_t i;
    memset(f, 0, CORE);
    f[0] = 0x7F; f[1] = 0xFE; f[2] = 0x80; f[3] = 0x01;
    f[4] = (uint8_t)(0x80 | 0x7C | ((nblks >> 6) & 1));           /* FTYPE 1, SHORT 31, CPF 0 */
    f[5] = (uint8_t)(((nblks & 63) << 2) | ((fsize >> 12) & 3));
    f[6] = (uint8_t)(fsize >> 4);
    f[7] = (uint8_t)(((fsize & 15) << 4) | (amode >> 2));
    f[8] = (uint8_t)(((amode & 3) << 6) | (sfreq << 2) | (rate >> 3));
    f[9] = (uint8_t)((rate & 7) << 5);
    f[10] = (uint8_t)(1 << 1);                                      /* LFF 1: an LFE channel */
    f[16] = (uint8_t)k; f[17] = (uint8_t)(k >> 8);
    for (i = 18; i < CORE; i++) f[i] = (uint8_t)(i * 7 + k);
}

/* An extension substream frame of `size` bytes (7.5.2, short header form). */
static void put_substream(buf* b, size_t size, int k)
{
    uint8_t s[4096];
    uint32_t bits;
    size_t i, hdr = 16;
    memset(s, 0, sizeof(s));
    s[0] = 0x64; s[1] = 0x58; s[2] = 0x20; s[3] = 0x25;
    /* nExtSSIndex 0, bHeaderSizeType 0, nuExtSSHeaderSize - 1 (8), nuExtSSFsize - 1 (16). */
    bits = ((uint32_t)(hdr - 1) << 16) | (uint32_t)(size - 1);       /* 24 bits after the 3 flag bits */
    s[5] = (uint8_t)(bits >> 19);
    s[6] = (uint8_t)(bits >> 11);
    s[7] = (uint8_t)(bits >> 3);
    s[8] = (uint8_t)(bits << 5);
    for (i = hdr; i < size; i++) s[i] = (uint8_t)(i + k);
    put(b, s, size);
}

/* `frames` core frames, each with a substream of a size that varies when `hd`. */
static void make_stream(buf* b, int frames, int hd, int swapped)
{
    int k;
    for (k = 0; k < frames; k++) {
        uint8_t f[CORE];
        core_frame(f, k);
        if (swapped) {
            size_t i;
            for (i = 0; i < CORE; i += 2) { uint8_t t = f[i]; f[i] = f[i + 1]; f[i + 1] = t; }
        }
        put(b, f, CORE);
        if (hd) put_substream(b, 200 + (size_t)(k % 5) * 64, k);
    }
}

/* The value the stand-in puts out for frame k of make_stream(hd, not swapped). */
static float expected(int k, int hd, int core_only)
{
    buf b = { 0 };
    uint8_t f[CORE];
    float v;
    core_frame(f, k);
    put(&b, f, CORE);
    if (hd && !core_only) put_substream(&b, 200 + (size_t)(k % 5) * 64, k);
    v = frame_value(b.p, b.n);
    free(b.p);
    return v;
}

/* ---- reading -------------------------------------------------------------------- */

static float* read_all(fa_stream* s, uint32_t channels, int64_t* out_n)
{
    int64_t cap = 1 << 16, n = 0;
    float* all = (float*)malloc((size_t)cap * channels * sizeof(float));
    for (;;) {
        fa_frames got = 0;
        fa_result r;
        if (n + 1024 > cap) {
            cap *= 2;
            all = (float*)realloc(all, (size_t)cap * channels * sizeof(float));
        }
        r = fa_stream_read(s, all + (size_t)n * channels, 1024, &got);
        n += got;
        if (r != FA_OK || got == 0) break;
    }
    *out_n = n;
    return all;
}

static fa_result open_memory(const buf* b, fa_stream** s)
{
    fa_open_options o;
    fa_open_options_init(&o);
    o.flags = FA_OPEN_DECODE_ONLY;
    *s = NULL;
    return fa_stream_open_memory(g_engine, b->p, b->n, &o, s);
}

static fa_result open_file(const char* name, fa_stream** s)
{
    char path[512];
    fa_open_options o;
    fa_open_options_init(&o);
    o.flags = FA_OPEN_DECODE_ONLY;
    snprintf(path, sizeof(path), "%s/%s", FA_TEST_DATA_DIR, name);
    *s = NULL;
    return fa_stream_open_path(g_engine, path, &o, s);
}

/* Seeks to `at` and checks the next `count` frames against the full read. */
static int seek_matches(fa_stream* s, const float* full, int64_t n, int64_t at, int64_t count, uint32_t ch)
{
    float* got = (float*)malloc((size_t)count * ch * sizeof(float));
    int64_t have = 0, i;
    int ok = 1;
    if (fa_stream_seek(s, at) != FA_OK) { free(got); return 0; }
    while (have < count) {
        fa_frames g = 0;
        fa_frames want = (fa_frames)(count - have);
        if (fa_stream_read(s, got + (size_t)have * ch, want, &g) != FA_OK || g == 0) break;
        have += g;
    }
    if (have != count || at + count > n) ok = 0;
    for (i = 0; ok && i < count * ch; i++)
        if (got[i] != full[(size_t)at * ch + (size_t)i]) ok = 0;
    free(got);
    return ok;
}

/* ---- tests ------------------------------------------------------------------------- */

/* A raw DTS core stream: the reader's length, frames in order, exact seeks. */
static void test_raw_core(void)
{
    buf b = { 0 };
    fa_stream* s = NULL;
    fa_stream_info info = { .struct_size = sizeof(info) };
    float* all;
    int64_t n;
    int k;
    use_fake(1);
    make_stream(&b, 40, 0, 0);
    CHECK_EQ(open_memory(&b, &s), FA_OK);
    CHECK_STR(g_opened, "dtshd");
    CHECK_EQ(fa_stream_get_info(s, &info), FA_OK);
    CHECK_STR(info.codec, "dts");
    CHECK_STR(info.container, "dts");
    CHECK_EQ(info.format.sample_rate, 48000);
    CHECK_EQ(info.format.channels, 6);
    CHECK_EQ(info.length_frames, 40 * 512);
    CHECK_EQ(info.can_decode, 1);
    CHECK_EQ(info.seekable, 1);
    CHECK_EQ(info.bitrate, 768000);                                  /* 1024 bytes per 512 samples */
    all = read_all(s, 6, &n);
    CHECK_EQ(n, 40 * 512);
    CHECK_EQ(g_bad, 0);
    for (k = 0; k < 40; k++) CHECK(all[(size_t)k * 512 * 6] == expected(k, 0, 0) && all[((size_t)k * 512 + 511) * 6 + 5] == expected(k, 0, 0));
    CHECK(seek_matches(s, all, n, 7000, 3000, 6));
    CHECK(seek_matches(s, all, n, 0, 600, 6));
    CHECK(seek_matches(s, all, n, 40 * 512 - 700, 700, 6));
    free(all);
    fa_stream_close(s);
    free(b.p);
}

/* DTS-HD frames (substreams of varying size, so no constant frame size): a DTS-HD
 * decoder gets core and substream together, a core decoder only the core. */
static void test_raw_hd(void)
{
    int pass;
    for (pass = 0; pass < 2; pass++) {
        buf b = { 0 };
        fa_stream* s = NULL;
        fa_stream_info info = { .struct_size = sizeof(info) };
        float* all;
        int64_t n;
        int k;
        use_fake(pass == 0);
        make_stream(&b, 30, 1, 0);
        CHECK_EQ(open_memory(&b, &s), FA_OK);
        CHECK_STR(g_opened, pass == 0 ? "dtshd" : "dts");
        CHECK_EQ(fa_stream_get_info(s, &info), FA_OK);
        CHECK_EQ(info.length_frames, 30 * 512);
        all = read_all(s, 6, &n);
        CHECK_EQ(n, 30 * 512);
        CHECK_EQ(g_bad, 0);
        if (pass == 1) CHECK_EQ(g_last_size, CORE);
        for (k = 0; k < 30; k++) CHECK(all[(size_t)k * 512 * 6] == expected(k, 1, pass == 1));
        CHECK(seek_matches(s, all, n, 5555, 2000, 6));
        CHECK(seek_matches(s, all, n, 30 * 512 - 1000, 1000, 6));
        free(all);
        fa_stream_close(s);
        free(b.p);
    }
}

/* 16-bit words in little-endian order: the decoder gets them big-endian, so they play
 * as the same frames. */
static void test_raw_swapped(void)
{
    buf b = { 0 };
    fa_stream* s = NULL;
    float* all;
    int64_t n;
    int k;
    use_fake(1);
    make_stream(&b, 12, 0, 1);
    CHECK_EQ(open_memory(&b, &s), FA_OK);
    all = read_all(s, 6, &n);
    CHECK_EQ(n, 12 * 512);
    CHECK_EQ(g_bad, 0);
    for (k = 0; k < 12; k++) CHECK(all[(size_t)k * 512 * 6] == expected(k, 0, 0));
    free(all);
    fa_stream_close(s);
    free(b.p);
}

/* An ID3v2 tag and junk before the first frame, junk between frames and a frame cut
 * short at the end. */
static void test_raw_junk(void)
{
    buf b = { 0 }, tail = { 0 };
    fa_stream* s = NULL;
    fa_stream_info info = { .struct_size = sizeof(info) };
    uint8_t id3[10 + 300] = { 'I', 'D', '3', 3, 0, 0, 0, 0, 0x02, 0x2C };   /* 300 bytes of tag */
    uint8_t junk[77];
    float* all;
    int64_t n;
    int k;
    use_fake(1);
    memset(junk, 0x55, sizeof(junk));
    put(&b, id3, sizeof(id3));
    put(&b, junk, sizeof(junk));
    make_stream(&tail, 10, 0, 0);
    put(&b, tail.p, 5 * CORE);
    put(&b, junk, sizeof(junk));
    put(&b, tail.p + 5 * CORE, 5 * CORE);
    put(&b, tail.p, 300);                                               /* a frame cut short */
    CHECK_EQ(open_memory(&b, &s), FA_OK);
    CHECK_EQ(fa_stream_get_info(s, &info), FA_OK);
    CHECK_EQ(info.length_frames, 10 * 512);
    all = read_all(s, 6, &n);
    CHECK_EQ(n, 10 * 512);
    for (k = 0; k < 10; k++) CHECK(all[(size_t)k * 512 * 6] == expected(k, 0, 0));
    CHECK(seek_matches(s, all, n, 3000, 1500, 6));
    free(all);
    fa_stream_close(s);
    free(b.p);
    free(tail.p);
}

/* The packet codec on its own: packets cut anywhere (an AVI chunk need not hold
 * whole frames) still reach the decoder as whole frames, the last one at the end. */
static void test_codec_framing(void)
{
    buf b = { 0 };
    fa_codec c;
    fa_codec_params p;
    size_t at = 0, step = 333;
    int64_t frames = 0, k = 0;
    int ok = 1;
    use_fake(1);
    make_stream(&b, 20, 1, 0);
    fa_codec_params_init(&p);
    p.codec = "dts";
    p.sample_rate = 48000;
    p.channels = 6;
    CHECK_EQ(fa_codec_open(&c, &p), FA_OK);
    while (at <= b.n) {
        size_t len = at + step <= b.n ? step : b.n - at;
        fa_result r = len > 0 ? c.vt->decode(&c, b.p + at, len) : c.vt->decode(&c, NULL, 0);
        fa_frames i;
        CHECK_EQ(r, FA_OK);
        for (i = 0; i < c.out_frames; i += 512, k++)
            if (c.out[(size_t)i * c.channels] != expected((int)k, 1, 0)) ok = 0;
        frames += c.out_frames;
        if (len == 0) break;
        at += len;
        step = step == 333 ? 2900 : 333;
    }
    CHECK(ok);
    CHECK_EQ(frames, 20 * 512);
    CHECK_EQ(g_units, 20);
    CHECK_EQ(g_bad, 0);
    fa_codec_close(&c);
    free(b.p);
}

/* Without a system decoder (Linux; most Windows and Android machines) a DTS file is
 * refused, and the tags reader still gives its details. */
static void test_no_system_decoder(void)
{
    buf b = { 0 };
    fa_stream* s = NULL;
    fa_tags* t = NULL;
    fa_stream_info info = { .struct_size = sizeof(info) };
    fa_test_system_codec = NULL;
    make_stream(&b, 10, 0, 0);
#if !defined(_WIN32) && !defined(__ANDROID__)
    CHECK_EQ(open_memory(&b, &s), FA_ERROR_UNSUPPORTED_FORMAT);
    CHECK(s == NULL);
#else
    {
        /* A machine may have a DTS decoder: say which (the made-up frames need not decode). */
        fa_result r = open_memory(&b, &s);
        printf("  system DTS decoder: %s (%d)\n", r == FA_OK ? "yes" : "no", (int)r);
        if (s) fa_stream_close(s);
    }
#endif
    CHECK_EQ(fa_tags_read_memory(b.p, b.n, 0, &t), FA_OK);
    CHECK_EQ(fa_tags_get_info(t, &info), FA_OK);
    CHECK_STR(info.codec, "dts");
    CHECK_EQ(info.length_frames, 10 * 512);
    fa_tags_close(t);
    free(b.p);
}

/* FFmpeg-made files: raw, MPEG program and transport streams, AVI and Matroska. Each
 * plays every frame (0.6 s, 57 frames of 512 samples) and seeks exactly. */
static void test_containers(void)
{
    static const char* files[] = { "dts/tone.dts", "dts/ps_dts.vob", "mpeg/ts_dts.ts", "dts/dts.avi", "dts/dts.mkv" };
    static const double seconds[] = { 0.6, 0.6, 2.0, 0.6, 0.6 };   /* ts_dts.ts: make_mpeg_data.sh, 2 s of stereo */
    size_t i;
    for (i = 0; i < sizeof(files) / sizeof(files[0]); i++) {
        fa_stream* s = NULL;
        fa_stream_info info = { .struct_size = sizeof(info) };
        float* all;
        int64_t n;
        uint32_t ch;
        use_fake(1);
        printf("  %s\n", files[i]);
        CHECK_EQ(open_file(files[i], &s), FA_OK);
        CHECK_EQ(fa_stream_get_info(s, &info), FA_OK);
        CHECK_STR(info.codec, "dts");
        CHECK_EQ(info.format.sample_rate, 48000);
        ch = info.format.channels;
        all = read_all(s, ch, &n);
        CHECK_EQ(g_bad, 0);
        CHECK(n >= (int64_t)(seconds[i] * 48000) - 1024 && n <= (int64_t)(seconds[i] * 48000) + 1536);
        if (info.length_frames > 0) CHECK_EQ(n, info.length_frames);
        CHECK(seek_matches(s, all, n, 15000, 4000, ch));
        CHECK(seek_matches(s, all, n, 100, 2000, ch));
        free(all);
        fa_stream_close(s);
    }
}

int main(void)
{
    fa_engine_config cfg;
    fa_engine_config_init(&cfg);
    cfg.flags = FA_ENGINE_NULL_OUTPUT;
    cfg.worker_threads = 2;
    if (fa_engine_create(&cfg, &g_engine) != FA_OK) { fprintf(stderr, "engine create failed\n"); return 1; }
    RUN(test_raw_core);
    RUN(test_raw_hd);
    RUN(test_raw_swapped);
    RUN(test_raw_junk);
    RUN(test_codec_framing);
    RUN(test_containers);
    RUN(test_no_system_decoder);
    fa_test_system_codec = NULL;
    fa_engine_destroy(g_engine);
    if (g_failures) { fprintf(stderr, "%d failure(s)\n", g_failures); return 1; }
    printf("all passed\n");
    return 0;
}
