/* Loudness normalization tests: the BS.1770 / R128 meter against the EBU
 * compliance cases (Tech 3341 / 3342), true peak, chunked scans versus one pass,
 * the cache, gain tags, the mixer's normalization gain and glide, and the
 * output limiter. Like test_internal, the device is never started: the test
 * calls the mixer itself so every output sample can be checked. */
#if !defined(_WIN32)
#  define _POSIX_C_SOURCE 200809L
#endif
#include "fa_internal.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#if defined(__APPLE__)
#  include <unistd.h>
#endif

#include "test_util.h"

#define PI 3.14159265358979323846

static fa_engine* g_engine;
static fa_device* g_dev;

/* ---- signals ------------------------------------------------------------------ */

/* A piecewise signal: segments of a sine (or noise) at a level in dBFS
 * (peak amplitude of the sine), the same in every channel. */
typedef struct segment { double seconds, dbfs; int noise; } segment;

static float* make_signal(uint32_t rate, uint32_t ch, const segment* seg, int nseg, double freq, int64_t* out_frames)
{
    int64_t total = 0, at = 0, i;
    int s;
    uint32_t c, rng = 12345;
    float* x;
    for (s = 0; s < nseg; s++) total += (int64_t)(seg[s].seconds * rate + 0.5);
    x = (float*)malloc((size_t)total * ch * sizeof(float));
    for (s = 0; s < nseg; s++) {
        int64_t n = (int64_t)(seg[s].seconds * rate + 0.5);
        double a = pow(10.0, seg[s].dbfs / 20.0);
        for (i = 0; i < n; i++, at++) {
            float v;
            if (seg[s].noise) {
                rng = rng * 1664525u + 1013904223u;
                v = (float)(a * ((double)(rng >> 8) / 8388608.0 - 1.0));
            } else {
                v = (float)(a * sin(2.0 * PI * freq * (double)at / rate));
            }
            for (c = 0; c < ch; c++) x[at * ch + c] = v;
        }
    }
    *out_frames = total;
    return x;
}

/* 32-bit float WAV, optionally followed by an APEv2 tag. */
static uint8_t* make_wav(uint32_t rate, uint32_t ch, const float* x, int64_t frames, const uint8_t* tail, size_t tail_n, size_t* size)
{
    size_t data = (size_t)frames * ch * 4, n = 44 + data + tail_n;
    uint8_t* b = (uint8_t*)malloc(n);
    uint32_t v;
    uint16_t w;
    memcpy(b, "RIFF", 4); v = (uint32_t)(36 + data); memcpy(b + 4, &v, 4); memcpy(b + 8, "WAVEfmt ", 8);
    v = 16; memcpy(b + 16, &v, 4);
    w = 3; memcpy(b + 20, &w, 2);                               /* IEEE float */
    w = (uint16_t)ch; memcpy(b + 22, &w, 2);
    memcpy(b + 24, &rate, 4); v = rate * ch * 4; memcpy(b + 28, &v, 4);
    w = (uint16_t)(ch * 4); memcpy(b + 32, &w, 2); w = 32; memcpy(b + 34, &w, 2);
    memcpy(b + 36, "data", 4); v = (uint32_t)data; memcpy(b + 40, &v, 4);
    memcpy(b + 44, x, data);
    if (tail_n) memcpy(b + 44 + data, tail, tail_n);
    *size = n;
    return b;
}

static int write_file(const char* path, const uint8_t* b, size_t n)
{
    FILE* f = fopen(path, "wb");
    if (!f) return 0;
    fwrite(b, 1, n, f);
    fclose(f);
    return 1;
}

/* Integrated loudness, range and true peak of a buffer, in one pass. */
static void measure(const float* x, int64_t frames, uint32_t rate, uint32_t ch, double* i_lufs, double* lra, double* tp)
{
    fa_meter m;
    fa_meter_init(&m, rate, ch, 1, 0);
    fa_meter_process(&m, x, frames);
    fa_truepeak_flush(m.tp);
    *i_lufs = fa_loudness_integrated(m.subs, m.nsubs);
    if (lra) *lra = fa_loudness_range(m.subs, m.nsubs);
    if (tp) *tp = fa_meter_peak_dbtp(&m);
    fa_meter_free(&m);
}

/* ---- meter: EBU Tech 3341 / 3342 ---------------------------------------------------- */

static void check_case(const segment* seg, int nseg, double expect, uint32_t rate)
{
    int64_t n;
    double i;
    float* x = make_signal(rate, 2, seg, nseg, 1000.0, &n);
    measure(x, n, rate, 2, &i, NULL, NULL);
    free(x);
    CHECK_NEAR(i, expect, 0.1);
}

static void test_ebu_3341_integrated(void)
{
    static const segment c1[] = { { 20, -23, 0 } };
    static const segment c2[] = { { 20, -33, 0 } };
    static const segment c3[] = { { 10, -36, 0 }, { 60, -23, 0 }, { 10, -36, 0 } };
    static const segment c4[] = { { 10, -72, 0 }, { 10, -36, 0 }, { 60, -23, 0 }, { 10, -36, 0 }, { 10, -72, 0 } };
    uint32_t rates[2] = { 48000, 44100 };
    int r;
    for (r = 0; r < 2; r++) {
        int before = g_failures;
        check_case(c1, 1, -23.0, rates[r]); if (g_failures != before) return;
        check_case(c2, 1, -33.0, rates[r]); if (g_failures != before) return;
        check_case(c3, 3, -23.0, rates[r]); if (g_failures != before) return;
        check_case(c4, 5, -23.0, rates[r]); if (g_failures != before) return;
    }
}

static void test_ebu_3342_range(void)
{
    static const segment c1[] = { { 20, -20, 0 }, { 20, -30, 0 } };
    static const segment c2[] = { { 20, -20, 0 }, { 20, -15, 0 } };
    static const segment c3[] = { { 20, -40, 0 }, { 20, -20, 0 } };
    int64_t n;
    double i, lra;
    float* x;
    x = make_signal(48000, 2, c1, 2, 1000.0, &n); measure(x, n, 48000, 2, &i, &lra, NULL); free(x);
    CHECK_NEAR(lra, 10.0, 1.0);
    x = make_signal(48000, 2, c2, 2, 1000.0, &n); measure(x, n, 48000, 2, &i, &lra, NULL); free(x);
    CHECK_NEAR(lra, 5.0, 1.0);
    x = make_signal(48000, 2, c3, 2, 1000.0, &n); measure(x, n, 48000, 2, &i, &lra, NULL); free(x);
    CHECK_NEAR(lra, 20.0, 1.0);
}

static void test_true_peak(void)
{
    /* A sine at a quarter of the sample rate, 45 degrees out of phase with the
     * samples: every sample is at 0.707 of the real peak (-3 dB). */
    uint32_t rate = 48000;
    int64_t n = rate * 2, k;
    float* x = (float*)malloc((size_t)n * 2 * sizeof(float));
    double i, tp, sample_peak = 0.0;
    for (k = 0; k < n; k++) {
        float v = (float)(0.5 * sin(2.0 * PI * (double)k / 4.0 + PI / 4.0));
        x[k * 2] = x[k * 2 + 1] = v;
        if (fabs(v) > sample_peak) sample_peak = fabs(v);
    }
    measure(x, n, rate, 2, &i, NULL, &tp);
    free(x);
    CHECK_NEAR(20.0 * log10(sample_peak), -9.03, 0.05);
    CHECK_NEAR(tp, -6.02, 0.3);             /* BS.1770 tolerance is +0.2 / -0.4 dB */
}

/* Sine at `freq` and `phase` (radians), 0.5 peak: returns sample peak and true peak in dB. */
static void sine_peaks(double freq, double phase, double* sample_db, double* tp)
{
    uint32_t rate = 48000;
    int64_t n = rate, k;
    float* x = (float*)malloc((size_t)n * 2 * sizeof(float));
    double i, sp = 0.0;
    for (k = 0; k < n; k++) {
        /* 10 ms fades: an abrupt start or end overshoots for real (Gibbs). */
        double fade = k < 480 ? k / 480.0 : n - 1 - k < 480 ? (n - 1 - k) / 480.0 : 1.0;
        float v = (float)(0.5 * fade * sin(2.0 * PI * freq * (double)k / rate + phase));
        x[k * 2] = x[k * 2 + 1] = v;
        if (fabs(v) > sp) sp = fabs(v);
    }
    measure(x, n, rate, 2, &i, NULL, tp);
    free(x);
    *sample_db = 20.0 * log10(sp);
}

static void test_true_peak_high_frequencies(void)
{
    double sp, tp;
    /* 16 kHz (fs/3) in phase 0: samples sit 30 degrees off the crests (-1.25 dB). */
    sine_peaks(16000.0, 0.0, &sp, &tp);
    CHECK_NEAR(sp, -7.27, 0.05);
    CHECK(tp > sp + 0.8);
    CHECK_NEAR(tp, -6.02, 0.4);             /* 4x oversampling: up to -0.3 dB here */
    /* 20 kHz at 48 kHz, 0.83x Nyquist: the interpolator must still be flat there. */
    sine_peaks(20000.0, 15.0 * PI / 180.0, &sp, &tp);
    CHECK_NEAR(sp, -6.32, 0.05);
    CHECK_NEAR(tp, -6.02, 0.15);
    /* Never reads high, at any frequency. */
    sine_peaks(997.0, 0.1, &sp, &tp);
    CHECK(tp <= -6.02 + 0.01);
    sine_peaks(21000.0, 0.7, &sp, &tp);
    CHECK(tp <= -6.02 + 0.01);
}

static void test_true_peak_includes_samples(void)
{
    /* A lone impulse: no interpolated point reaches the sample, the sample counts. */
    int64_t n = 4800;
    float* x = (float*)calloc((size_t)n * 2, sizeof(float));
    double i, tp;
    x[2400 * 2] = 0.9f;
    measure(x, n, 48000, 2, &i, NULL, &tp);
    free(x);
    CHECK(tp >= 20.0 * log10(0.9) - 1e-4);
}

static void test_true_peak_at_end_of_file(void)
{
    /* Silence ending in two samples at 0.5: the peak between them (about -3.9 dBTP)
     * is only seen if the scan flushes the oversampler at the end. */
    uint32_t rate = 48000;
    int64_t n = rate, k;
    float* x = (float*)calloc((size_t)n, sizeof(float));
    size_t size;
    uint8_t* wav;
    fa_loudness l = { .struct_size = sizeof(l) };
    x[n - 2] = x[n - 1] = 0.5f;
    wav = make_wav(rate, 1, x, n, NULL, 0, &size);
    CHECK(write_file("loudness-eof.wav", wav, size));
    free(wav);
    free(x);
    CHECK_EQ(fa_scan_loudness_path(g_engine, "loudness-eof.wav", &l), FA_OK);
    CHECK(l.true_peak_dbtp > -6.02 + 1.0);
    (void)k;
}

static void test_surround_weights(void)
{
    /* Same sine in L only vs in Ls only: the surround channel weighs +1.5 dB. */
    int64_t n = 48000 * 10, k;
    float* x = (float*)calloc((size_t)n * 6, sizeof(float));
    double front, surround;
    for (k = 0; k < n; k++) x[k * 6 + 0] = (float)(0.1 * sin(2.0 * PI * 1000.0 * k / 48000.0));
    measure(x, n, 48000, 6, &front, NULL, NULL);
    memset(x, 0, (size_t)n * 6 * sizeof(float));
    for (k = 0; k < n; k++) x[k * 6 + 4] = (float)(0.1 * sin(2.0 * PI * 1000.0 * k / 48000.0));
    measure(x, n, 48000, 6, &surround, NULL, NULL);
    for (k = 0; k < n; k++) { x[k * 6 + 4] = 0.0f; x[k * 6 + 3] = (float)(0.5 * sin(2.0 * PI * 50.0 * k / 48000.0)); }
    {
        double lfe;
        measure(x, n, 48000, 6, &lfe, NULL, NULL);
        CHECK(lfe < -1000.0);                /* LFE is not counted */
    }
    free(x);
    CHECK_NEAR(surround - front, 10.0 * log10(1.41), 0.01);
}

/* Loudness of a 1 kHz sine in channel `c` only, of `channels`. */
static double one_channel(uint32_t channels, uint32_t c)
{
    int64_t n = 48000 * 3, k;
    float* x = (float*)calloc((size_t)n * channels, sizeof(float));
    double l;
    for (k = 0; k < n; k++) x[k * channels + c] = (float)(0.1 * sin(2.0 * PI * 1000.0 * k / 48000.0));
    measure(x, n, 48000, channels, &l, NULL, NULL);
    free(x);
    return l;
}

static void test_quad_and_61_weights(void)
{
    double plus = 10.0 * log10(1.41);
    double front = one_channel(4, 0);
    CHECK_NEAR(one_channel(4, 2) - front, plus, 0.01);    /* quad: L R Ls Rs */
    CHECK_NEAR(one_channel(4, 3) - front, plus, 0.01);
    front = one_channel(7, 2);
    CHECK(one_channel(7, 3) < -1000.0);                   /* 6.1: LFE excluded */
    CHECK_NEAR(one_channel(7, 4) - front, plus, 0.01);
    CHECK_NEAR(one_channel(7, 6) - front, plus, 0.01);
}

/* ---- scans ------------------------------------------------------------------------- */

/* Three minutes of noise and tones at changing levels. */
static const segment g_song[] = {
    { 25, -30, 1 }, { 40, -14, 1 }, { 20, -26, 0 }, { 50, -18, 1 }, { 30, -40, 1 }, { 15, -12, 0 }
};

static void test_chunked_scan_matches_one_pass(void)
{
    uint32_t rate = 44100;
    int64_t n;
    size_t size;
    float* x = make_signal(rate, 2, g_song, 6, 440.0, &n);
    uint8_t* wav = make_wav(rate, 2, x, n, NULL, 0, &size);
    double i, lra, tp;
    fa_loudness l = { .struct_size = sizeof(l) };
    int64_t t0;
    CHECK(write_file("loudness-song.wav", wav, size));
    measure(x, n, rate, 2, &i, &lra, &tp);
    free(x);
    free(wav);

    t0 = fa_monotonic_ns();
    CHECK_EQ(fa_scan_loudness_path(g_engine, "loudness-song.wav", &l), FA_OK);
    printf("  scan of %.0f s of audio: %.1f ms (%u scan threads + caller)\n", (double)n / rate,
           (double)(fa_monotonic_ns() - t0) / 1e6, g_engine->scan_thread_count);
    CHECK_EQ(l.is_final, 1);
    CHECK_EQ(l.source, FA_LOUDNESS_SCAN);
    CHECK_NEAR(l.integrated_lufs, i, 0.01);
    CHECK_NEAR(l.range_lu, lra, 0.05);
    CHECK_NEAR(l.true_peak_dbtp, tp, 0.001);
    CHECK_EQ(l.gain_db, 0);                  /* normalization is off */
}

static void test_cache(void)
{
    fa_engine_config cfg;
    fa_engine* e;
    fa_loudness a = { .struct_size = sizeof(a) }, b = { .struct_size = sizeof(b) };
    char* dir = (char*)malloc(32);
    fa_engine_config_init(&cfg);
    cfg.flags = FA_ENGINE_NULL_OUTPUT;
    strcpy(dir, "loudness-cache");
    cfg.cache_directory = dir;
    CHECK_EQ(fa_engine_create(&cfg, &e), FA_OK);
    memset(dir, 'x', 31);                     /* the engine keeps its own copy */
    free(dir);
    /* Start clean: a previous run may have left this entry. */
    {
        fa_loudness first = { .struct_size = sizeof(first) };
        fa_scan_loudness_path(e, "loudness-song.wav", &first);
    }
    CHECK_EQ(fa_scan_loudness_path(e, "loudness-song.wav", &a), FA_OK);
    CHECK_EQ(a.source, FA_LOUDNESS_CACHE);
    fa_engine_destroy(e);
    CHECK_EQ(fa_scan_loudness_path(g_engine, "loudness-song.wav", &b), FA_OK);  /* no cache here */
    CHECK_EQ(b.source, FA_LOUDNESS_SCAN);
    CHECK_NEAR(a.integrated_lufs, b.integrated_lufs, 0.001);
    CHECK_NEAR(a.range_lu, b.range_lu, 0.001);
    CHECK_NEAR(a.true_peak_dbtp, b.true_peak_dbtp, 0.001);
}

static void test_scan_errors(void)
{
    fa_loudness l = { .struct_size = sizeof(l) };
    CHECK_EQ(fa_scan_loudness_path(g_engine, "does-not-exist.wav", &l), FA_ERROR_NOT_FOUND);
    CHECK(write_file("loudness-junk.bin", (const uint8_t*)"this is not audio at all", 24));
    CHECK_EQ(fa_scan_loudness_path(g_engine, "loudness-junk.bin", &l), FA_ERROR_UNSUPPORTED_FORMAT);
    CHECK_EQ(fa_scan_loudness_path(NULL, "x", &l), FA_ERROR_INVALID_ARG);
}

/* ---- tags --------------------------------------------------------------------------- */

static size_t put_be32(uint8_t* p, uint32_t v) { p[0] = (uint8_t)(v >> 24); p[1] = (uint8_t)(v >> 16); p[2] = (uint8_t)(v >> 8); p[3] = (uint8_t)v; return 4; }
static size_t put_le32(uint8_t* p, uint32_t v) { p[0] = (uint8_t)v; p[1] = (uint8_t)(v >> 8); p[2] = (uint8_t)(v >> 16); p[3] = (uint8_t)(v >> 24); return 4; }

/* ID3v2.3 with a big picture frame first, then two TXXX frames (one UTF-16). */
static size_t make_id3(uint8_t* b)
{
    size_t p = 10, start, tag;
    static const char latin[] = "\0REPLAYGAIN_TRACK_GAIN\0-6.50 dB";
    static const char key16[] = "REPLAYGAIN_ALBUM_GAIN";
    static const char val16[] = "+1.25 dB";
    size_t k;
    memcpy(b, "ID3\x03\x00\x00", 6);
    memcpy(b + p, "APIC", 4); put_be32(b + p + 4, 3000); b[p + 8] = b[p + 9] = 0;
    memset(b + p + 10, 0xAB, 3000);
    p += 10 + 3000;
    memcpy(b + p, "TXXX", 4); put_be32(b + p + 4, (uint32_t)(sizeof(latin) - 1)); b[p + 8] = b[p + 9] = 0;
    memcpy(b + p + 10, latin, sizeof(latin) - 1);
    p += 10 + sizeof(latin) - 1;
    start = p;
    memcpy(b + p, "TXXX", 4); b[p + 8] = b[p + 9] = 0;
    p += 10;
    b[p++] = 1;                                   /* UTF-16 with BOM */
    b[p++] = 0xFF; b[p++] = 0xFE;
    for (k = 0; key16[k]; k++) { b[p++] = (uint8_t)key16[k]; b[p++] = 0; }
    b[p++] = 0; b[p++] = 0;
    b[p++] = 0xFF; b[p++] = 0xFE;
    for (k = 0; val16[k]; k++) { b[p++] = (uint8_t)val16[k]; b[p++] = 0; }
    put_be32(b + start + 4, (uint32_t)(p - start - 10));
    memset(b + p, 0, 64);                         /* padding */
    p += 64;
    tag = p - 10;
    b[6] = (uint8_t)((tag >> 21) & 0x7f); b[7] = (uint8_t)((tag >> 14) & 0x7f);
    b[8] = (uint8_t)((tag >> 7) & 0x7f); b[9] = (uint8_t)(tag & 0x7f);
    return p;
}

/* APEv2 footer-only tag with one item. */
static size_t make_ape(uint8_t* b, const char* key, const char* value)
{
    size_t p = 0, vlen = strlen(value), items;
    p += put_le32(b + p, (uint32_t)vlen);
    p += put_le32(b + p, 0);
    memcpy(b + p, key, strlen(key) + 1); p += strlen(key) + 1;
    memcpy(b + p, value, vlen); p += vlen;
    items = p;
    memcpy(b + p, "APETAGEX", 8);
    put_le32(b + p + 8, 2000);
    put_le32(b + p + 12, (uint32_t)(items + 32));
    put_le32(b + p + 16, 1);
    put_le32(b + p + 20, 0);
    memset(b + p + 24, 0, 8);
    return p + 32;
}

static void test_tag_parsers(void)
{
    uint8_t buf[8192];
    fa_source src;
    fa_tag_loudness t;
    size_t n;

    n = make_id3(buf);
    fa_source_open_memory_ref(&src, buf, n);
    fa_tags_read_loudness(&src, &t);
    CHECK_NEAR(t.track_lufs, -18.0 + 6.5, 1e-9);
    CHECK_NEAR(t.album_lufs, -18.0 - 1.25, 1e-9);

    n = make_ape(buf, "R128_TRACK_GAIN", "-1280");   /* -5 dB relative to -23 LUFS */
    fa_source_open_memory_ref(&src, buf, n);
    fa_tags_read_loudness(&src, &t);
    CHECK_NEAR(t.track_lufs, -18.0, 1e-9);
    CHECK(t.album_lufs != t.album_lufs);

    {
        /* FLAC: fLaC + STREAMINFO (34 bytes) + VORBIS_COMMENT (last). */
        static const char* items[2] = { "replaygain_track_gain=-3.00 dB", "REPLAYGAIN_TRACK_PEAK=0.5" };
        size_t p = 0, vc, k;
        memcpy(buf, "fLaC", 4); p = 4;
        buf[p++] = 0; buf[p++] = 0; buf[p++] = 0; buf[p++] = 34;
        memset(buf + p, 0, 34); p += 34;
        vc = p;
        p += 4;
        p += put_le32(buf + p, 3); memcpy(buf + p, "abc", 3); p += 3;
        p += put_le32(buf + p, 2);
        for (k = 0; k < 2; k++) { p += put_le32(buf + p, (uint32_t)strlen(items[k])); memcpy(buf + p, items[k], strlen(items[k])); p += strlen(items[k]); }
        buf[vc] = 0x84;
        buf[vc + 1] = (uint8_t)((p - vc - 4) >> 16); buf[vc + 2] = (uint8_t)((p - vc - 4) >> 8); buf[vc + 3] = (uint8_t)(p - vc - 4);
        fa_source_open_memory_ref(&src, buf, p);
        fa_tags_read_loudness(&src, &t);
        CHECK_NEAR(t.track_lufs, -15.0, 1e-9);
        CHECK_NEAR(t.track_peak_db, -6.0206, 1e-3);
    }
    {
        /* Long lyrics before the gain: the whole comment block is read. */
        size_t lyr = 40000, cap = lyr + 256, p = 0, vc;
        uint8_t* b = (uint8_t*)malloc(cap);
        static const char* gain = "REPLAYGAIN_TRACK_GAIN=+2.00 dB";
        memcpy(b, "fLaC", 4); p = 4;
        b[p++] = 0; b[p++] = 0; b[p++] = 0; b[p++] = 34;
        memset(b + p, 0, 34); p += 34;
        vc = p;
        p += 4;
        p += put_le32(b + p, 3); memcpy(b + p, "abc", 3); p += 3;
        p += put_le32(b + p, 2);
        p += put_le32(b + p, (uint32_t)lyr); memcpy(b + p, "LYRICS=", 7); memset(b + p + 7, 'a', lyr - 7); p += lyr;
        p += put_le32(b + p, (uint32_t)strlen(gain)); memcpy(b + p, gain, strlen(gain)); p += strlen(gain);
        b[vc] = 0x84;
        b[vc + 1] = (uint8_t)((p - vc - 4) >> 16); b[vc + 2] = (uint8_t)((p - vc - 4) >> 8); b[vc + 3] = (uint8_t)(p - vc - 4);
        fa_source_open_memory_ref(&src, b, p);
        fa_tags_read_loudness(&src, &t);
        CHECK_NEAR(t.track_lufs, -20.0, 1e-9);
        free(b);
    }
}

/* ---- playback ------------------------------------------------------------------------ */

/* Up to a minute: an emulated arm64 Android scans a 20 s song in several seconds,
 * and this returns as soon as the value is final. */
static int wait_final(fa_stream* s, fa_loudness* l)
{
    int i;
    for (i = 0; i < 12000; i++) {
        fa_stream_get_loudness(s, l);
        if (l->is_final) return 1;
        fa_sleep_ms(5);
    }
    return 0;
}

static void render(fa_stream* s, float* out, uint32_t frames)
{
    int i;
    for (i = 0; i < 2000; i++) {
        if (fa_ring_readable(&s->ring) >= (int64_t)frames || fa_load32(&s->eof)) break;
        fa_sleep_ms(1);
    }
    memset(out, 0, (size_t)frames * 2 * sizeof(float));
    fa_fetch_add64(&g_dev->epoch, 1);
    fa_stream_mix(s, g_dev, out, frames);
    fa_fetch_add64(&g_dev->epoch, 1);
}

static double peak_of(const float* x, size_t n)
{
    double p = 0.0;
    size_t i;
    for (i = 0; i < n; i++) if (fabs(x[i]) > p) p = fabs(x[i]);
    return p;
}

/* A mono sine of `seconds` at `dbfs` peak, at the device rate. */
static fa_stream* open_sine(double seconds, double dbfs, uint32_t flags, const uint8_t* tail, size_t tail_n)
{
    segment seg = { 0, 0, 0 };
    int64_t n;
    size_t size;
    float* x;
    uint8_t* wav;
    fa_open_options o;
    fa_stream* s = NULL;
    seg.seconds = seconds;
    seg.dbfs = dbfs;
    x = make_signal(g_dev->rate, 1, &seg, 1, 1000.0, &n);
    wav = make_wav(g_dev->rate, 1, x, n, tail, tail_n, &size);
    fa_open_options_init(&o);
    o.flags = flags;
    if (fa_stream_open_memory(g_engine, wav, size, &o, &s) != FA_OK) s = NULL;
    free(x);
    free(wav);
    return s;
}

static void test_stream_gain_from_scan(void)
{
    /* Mono 1 kHz sine at -10 dBFS peak measures -13.0 LUFS; target -23 -> -10 dB. */
    fa_stream* s;
    fa_loudness l = { .struct_size = sizeof(l) };
    float out[2 * 4800];
    CHECK_EQ(fa_engine_set_normalization(g_engine, FA_NORMALIZE_TRACK, -23.0), FA_OK);
    s = open_sine(30.0, -10.0, 0, NULL, 0);
    CHECK(s != NULL);
    CHECK(wait_final(s, &l));
    CHECK_EQ(l.source, FA_LOUDNESS_SCAN);
    CHECK_NEAR(l.integrated_lufs, -13.0, 0.1);
    CHECK_NEAR(l.gain_db, -23.0 - l.integrated_lufs, 1e-4);
    CHECK_EQ(fa_stream_attach(s, g_dev, NULL), FA_OK);
    CHECK_EQ(fa_stream_play(s), FA_OK);
    render(s, out, 4800);
    /* Exact gain from the first sample: the result was known before playback. */
    CHECK_NEAR(peak_of(out, 2 * 4800), pow(10.0, (-10.0 + l.gain_db) / 20.0), 2e-3);
    fa_stream_close(s);
}

static void test_glide_to_new_loudness(void)
{
    /* While playing, the loudness changes by 12 dB: the gain glides at 12 dB/s down. */
    fa_stream* s;
    fa_loudness l = { .struct_size = sizeof(l) }, app = { .struct_size = sizeof(app) };
    float out[2 * 480];
    int i;
    double first_db, after_half, end_db;
    s = open_sine(10.0, -10.0, 0, NULL, 0);
    CHECK(s != NULL);
    CHECK(wait_final(s, &l));
    CHECK_EQ(fa_stream_attach(s, g_dev, NULL), FA_OK);
    CHECK_EQ(fa_stream_play(s), FA_OK);
    render(s, out, 480);
    first_db = 20.0 * log10(peak_of(out, 960));
    memset(&app, 0, sizeof(app));
    app.struct_size = sizeof(app);
    app.integrated_lufs = l.integrated_lufs + 12.0;
    app.range_lu = app.true_peak_dbtp = NAN;
    CHECK_EQ(fa_stream_set_loudness(s, &app), FA_OK);
    CHECK_EQ(fa_stream_get_loudness(s, &l), FA_OK);
    CHECK_EQ(l.source, FA_LOUDNESS_APP);
    for (i = 0; i < 50; i++) render(s, out, 480);   /* 0.5 s at 48 kHz */
    after_half = 20.0 * log10(peak_of(out, 960));
    CHECK_NEAR(first_db - after_half, 12.0 * 0.5 * 48000.0 / g_dev->rate, 0.4);
    for (i = 0; i < 100; i++) render(s, out, 480);
    end_db = 20.0 * log10(peak_of(out, 960));
    CHECK_NEAR(first_db - end_db, 12.0, 0.05);       /* arrived and stays */
    fa_stream_close(s);
}

static void test_tags_win_and_album_mode(void)
{
    uint8_t tail[256];
    size_t n = 0;
    fa_stream* s;
    fa_loudness l = { .struct_size = sizeof(l) };
    /* Both an R128 track gain (-> -18 LUFS) and a ReplayGain album gain (-> -20 LUFS). */
    {
        size_t a = make_ape(tail, "R128_TRACK_GAIN", "-1280");
        /* Two items: rebuild by hand with both. */
        static const char* k2 = "REPLAYGAIN_ALBUM_GAIN";
        static const char* v2 = "2.00 dB";
        uint8_t items[256];
        size_t p = a - 32;                               /* first item's bytes */
        memcpy(items, tail, p);
        p += put_le32(items + p, (uint32_t)strlen(v2));
        p += put_le32(items + p, 0);
        memcpy(items + p, k2, strlen(k2) + 1); p += strlen(k2) + 1;
        memcpy(items + p, v2, strlen(v2)); p += strlen(v2);
        memcpy(tail, items, p);
        memcpy(tail + p, "APETAGEX", 8);
        put_le32(tail + p + 8, 2000);
        put_le32(tail + p + 12, (uint32_t)(p + 32));
        put_le32(tail + p + 16, 2);
        put_le32(tail + p + 20, 0);
        memset(tail + p + 24, 0, 8);
        n = p + 32;
    }
    CHECK_EQ(fa_engine_set_normalization(g_engine, FA_NORMALIZE_TRACK, -16.0), FA_OK);
    s = open_sine(5.0, -10.0, 0, tail, n);
    CHECK(s != NULL);
    CHECK(wait_final(s, &l));
    CHECK_EQ(l.source, FA_LOUDNESS_TAGS);
    CHECK_NEAR(l.integrated_lufs, -18.0, 1e-6);
    CHECK_NEAR(l.gain_db, 2.0, 1e-4);
    CHECK_EQ(fa_engine_set_normalization(g_engine, FA_NORMALIZE_ALBUM, -16.0), FA_OK);
    CHECK_EQ(fa_stream_get_loudness(s, &l), FA_OK);
    CHECK_NEAR(l.gain_db, 4.0, 1e-4);                     /* album: -20 LUFS */
    fa_stream_close(s);
    CHECK_EQ(fa_engine_set_normalization(g_engine, FA_NORMALIZE_TRACK, -23.0), FA_OK);
}

static void test_opt_out_and_off(void)
{
    fa_stream* s;
    fa_loudness l = { .struct_size = sizeof(l) };
    float out[2 * 480];
    s = open_sine(5.0, -10.0, FA_OPEN_NO_NORMALIZE, NULL, 0);
    CHECK(s != NULL);
    CHECK(wait_final(s, &l));                     /* measurable on request */
    CHECK_EQ(l.gain_db, 0);
    CHECK_EQ(fa_stream_attach(s, g_dev, NULL), FA_OK);
    CHECK_EQ(fa_stream_play(s), FA_OK);
    render(s, out, 480);
    CHECK_NEAR(peak_of(out, 960), pow(10.0, -0.5), 2e-3);
    fa_stream_close(s);

    CHECK_EQ(fa_engine_set_normalization(g_engine, FA_NORMALIZE_OFF, 0.0), FA_OK);
    s = open_sine(5.0, -10.0, 0, NULL, 0);
    CHECK(s != NULL);
    CHECK(wait_final(s, &l));
    CHECK_EQ(l.gain_db, 0);
    fa_stream_close(s);
    CHECK_EQ(fa_engine_set_normalization(g_engine, FA_NORMALIZE_TRACK, -23.0), FA_OK);
    CHECK_EQ(fa_engine_set_normalization(g_engine, FA_NORMALIZE_TRACK, 5.0), FA_ERROR_INVALID_ARG);
    CHECK_EQ(fa_engine_set_normalization(g_engine, (fa_normalize_mode)7, -16.0), FA_ERROR_INVALID_ARG);
}

/* App I/O cannot be read twice, so it is never scanned: the running estimate
 * from the decoded audio is what normalization uses. */
typedef struct mem_reader { const uint8_t* p; size_t n, pos; } mem_reader;
static int64_t FA_CALL mr_read(void* u, void* buf, size_t bytes)
{
    mem_reader* m = (mem_reader*)u;
    size_t k = m->n - m->pos < bytes ? m->n - m->pos : bytes;
    memcpy(buf, m->p + m->pos, k);
    m->pos += k;
    return (int64_t)k;
}

static void test_running_estimate_for_io(void)
{
    segment seg = { 10.0, -10.0, 0 };
    int64_t n;
    size_t size;
    float* x = make_signal(g_dev->rate, 1, &seg, 1, 1000.0, &n);
    uint8_t* wav = make_wav(g_dev->rate, 1, x, n, NULL, 0, &size);
    mem_reader mr;
    fa_io_callbacks io;
    fa_stream* s = NULL;
    fa_loudness l = { .struct_size = sizeof(l) };
    float out[2 * 4800];
    int i;
    free(x);
    mr.p = wav; mr.n = size; mr.pos = 0;
    memset(&io, 0, sizeof(io));
    io.read = mr_read;
    CHECK_EQ(fa_stream_open_io(g_engine, &io, &mr, NULL, &s), FA_OK);
    CHECK_EQ(fa_stream_get_loudness(s, &l), FA_OK);
    CHECK_EQ(l.source, FA_LOUDNESS_NONE);
    CHECK_EQ(fa_stream_attach(s, g_dev, NULL), FA_OK);
    CHECK_EQ(fa_stream_play(s), FA_OK);
    for (i = 0; i < 10; i++) render(s, out, 4800);     /* 1 s */
    CHECK_EQ(fa_stream_get_loudness(s, &l), FA_OK);
    CHECK_EQ(l.source, FA_LOUDNESS_ESTIMATE);
    CHECK_EQ(l.is_final, 0);
    CHECK_NEAR(l.integrated_lufs, -13.0, 0.1);
    CHECK_NEAR(l.gain_db, -10.0, 0.1);
    fa_stream_close(s);
    free(wav);
}

static void test_close_during_scan(void)
{
    /* Closing streams whose scans are queued or running must not wait for the scans. */
    fa_stream* s[6];
    int i;
    int64_t t0;
    for (i = 0; i < 6; i++) { s[i] = open_sine(120.0, -20.0, 0, NULL, 0); CHECK(s[i] != NULL); }
    t0 = fa_monotonic_ns();
    for (i = 5; i >= 0; i--) fa_stream_close(s[i]);
    CHECK((fa_monotonic_ns() - t0) / 1000000 < 2000);
}

/* ---- the loudness cache file ---------------------------------------------------------- */

static fa_loudness lc_value(double lufs)
{
    fa_loudness l;
    memset(&l, 0, sizeof(l));
    l.struct_size = sizeof(l);
    l.integrated_lufs = lufs;
    l.range_lu = 3.5;
    l.true_peak_dbtp = -1.25;
    return l;
}

static long file_size(const char* path)
{
    FILE* f = fopen(path, "rb");
    long n;
    if (!f) return -1;
    fseek(f, 0, SEEK_END);
    n = ftell(f);
    fclose(f);
    return n;
}

static void test_lcache_keeps_recently_used(void)
{
    fa_lcache* c;
    fa_loudness l = lc_value(-9.0), got;
    uint64_t k;
    long size;
    fa_mkdir_utf8("lcache-lru");
    c = fa_lcache_new("lcache-lru");
    CHECK(c != NULL);
    fa_lcache_clear(c);
    fa_lcache_set_limit(c, 16 * 1024);              /* 341 records; rewritten down to 255 */
    for (k = 0; k < 300; k++) { l.integrated_lufs = -(double)k / 10.0; fa_lcache_put(c, k, 1000, -1, &l); }
    CHECK(fa_lcache_get(c, 0, 1000, -1, &got));     /* the first song, played again */
    CHECK_NEAR(got.integrated_lufs, 0.0, 1e-6);
    CHECK_NEAR(got.range_lu, 3.5, 1e-6);
    CHECK_NEAR(got.true_peak_dbtp, -1.25, 1e-6);
    for (k = 300; k < 400; k++) fa_lcache_put(c, k, 1000, -1, &l);
    size = file_size("lcache-lru/loudness.fac");
    CHECK(size > 0 && size <= 16 * 1024);
    CHECK(fa_lcache_get(c, 0, 1000, -1, &got));     /* used recently: kept */
    CHECK(!fa_lcache_get(c, 1, 1000, -1, &got));    /* the least recently used went first */
    CHECK(fa_lcache_get(c, 399, 1000, -1, &got));
    CHECK(!fa_lcache_get(c, 0, 1001, -1, &got));    /* the size is part of the key */
    CHECK(!fa_lcache_get(c, 0, 1000, 2, &got));     /* and the audio track */
    fa_lcache_free(c);

    /* A new reader (another run of the app) sees the same. */
    c = fa_lcache_new("lcache-lru");
    fa_lcache_set_limit(c, 16 * 1024);
    CHECK(fa_lcache_get(c, 0, 1000, -1, &got));
    CHECK(!fa_lcache_get(c, 1, 1000, -1, &got));
    CHECK(fa_lcache_get(c, 399, 1000, -1, &got));
    fa_lcache_clear(c);
    CHECK_EQ(file_size("lcache-lru/loudness.fac"), -1);
    CHECK(!fa_lcache_get(c, 399, 1000, -1, &got));
    fa_lcache_free(c);
}

static void test_lcache_two_writers_and_a_torn_record(void)
{
    fa_lcache *a, *b;
    fa_loudness l = lc_value(-11.0), got;
    FILE* f;
    fa_mkdir_utf8("lcache-share");
    a = fa_lcache_new("lcache-share");
    b = fa_lcache_new("lcache-share/.");               /* another spelling: as another process would */
    CHECK(a && b && a != b);
    fa_lcache_clear(a);
    fa_lcache_put(a, 1, 10, -1, &l);
    l.integrated_lufs = -12.0;
    fa_lcache_put(b, 2, 10, -1, &l);                 /* b reads the file now, with a's song */
    l.integrated_lufs = -13.0;
    fa_lcache_put(a, 3, 10, -1, &l);
    /* A crash halfway through an append: a cut record, then more appends after it. */
    f = fopen("lcache-share/loudness.fac", "ab");
    CHECK(f != NULL);
    fwrite("FALR\1\2\3\4\5\6\7", 1, 11, f);
    fclose(f);
    l.integrated_lufs = -14.0;
    fa_lcache_put(b, 4, 10, -1, &l);
    fa_lcache_free(a);
    fa_lcache_free(b);

    a = fa_lcache_new("lcache-share");
    CHECK(fa_lcache_get(a, 1, 10, -1, &got)); CHECK_NEAR(got.integrated_lufs, -11.0, 1e-6);
    CHECK(fa_lcache_get(a, 2, 10, -1, &got)); CHECK_NEAR(got.integrated_lufs, -12.0, 1e-6);
    CHECK(fa_lcache_get(a, 3, 10, -1, &got)); CHECK_NEAR(got.integrated_lufs, -13.0, 1e-6);
    CHECK(fa_lcache_get(a, 4, 10, -1, &got)); CHECK_NEAR(got.integrated_lufs, -14.0, 1e-6);
    /* The damaged file was rewritten clean on load. */
    CHECK_EQ(file_size("lcache-share/loudness.fac"), 16 + 4 * 48);
    fa_lcache_clear(a);
    fa_lcache_free(a);
}

/* Engines on one folder share one cache, however the folder is spelled. */
static void test_lcache_one_per_folder(void)
{
    fa_lcache *a, *b;
    fa_engine_config cfg;
    fa_engine *e1, *e2;
    fa_loudness l = lc_value(-9.5), got;
    fa_mkdir_utf8("lcache-one");
    a = fa_lcache_new("lcache-one");
    b = fa_lcache_new("lcache-one/");
    CHECK(a && a == b);
    fa_lcache_clear(a);
    fa_lcache_put(a, 7, 10, -1, &l);
    fa_lcache_free(a);                                /* b still holds it */
    CHECK(fa_lcache_get(b, 7, 10, -1, &got)); CHECK_NEAR(got.integrated_lufs, -9.5, 1e-6);
    fa_lcache_free(b);

    fa_engine_config_init(&cfg);
    cfg.flags = FA_ENGINE_NULL_OUTPUT;
    cfg.cache_directory = "lcache-one";
    CHECK_EQ(fa_engine_create(&cfg, &e1), FA_OK);
    cfg.cache_directory = "lcache-one/";
    CHECK_EQ(fa_engine_create(&cfg, &e2), FA_OK);
    CHECK(e1->lcache && e1->lcache == e2->lcache);
    fa_engine_destroy(e1);
    CHECK(fa_lcache_get(e2->lcache, 7, 10, -1, &got));   /* from the file, by the engine left */
    CHECK_EQ(fa_engine_clear_cache(e2), FA_OK);
    fa_engine_destroy(e2);
}

/* The cache folder: every missing parent is made, a folder that cannot be made is reported (the engine
 * still starts), an unset one is the OS cache folder, FA_ENGINE_NO_CACHE means none. */
static void test_cache_folder_and_info(void)
{
    fa_engine_config cfg;
    fa_engine* e;
    fa_cache_info info;
    FILE* f;
    const char* blocker = "lcache-blocker";
    fa_engine_config_init(&cfg);
    cfg.flags = FA_ENGINE_NULL_OUTPUT;

    cfg.cache_directory = "lcache-deep/a/b/c";
    CHECK_EQ(fa_engine_create(&cfg, &e), FA_OK);
    memset(&info, 0, sizeof(info)); info.struct_size = sizeof(info);
    CHECK_EQ(fa_engine_get_cache_info(e, &info), FA_OK);
    CHECK_EQ(info.status, FA_OK);
    CHECK(info.directory && strcmp(info.directory, "lcache-deep/a/b/c") == 0);
    CHECK_EQ(info.is_default, 0);
    f = fa_fopen_utf8("lcache-deep/a/b/c/probe.tmp", "wb");   /* the folder is there */
    CHECK(f != NULL);
    if (f) { fclose(f); remove("lcache-deep/a/b/c/probe.tmp"); }
    fa_engine_destroy(e);

    f = fopen(blocker, "wb");                      /* a file where a folder is needed */
    CHECK(f != NULL);
    if (f) fclose(f);
    cfg.cache_directory = "lcache-blocker/sub/dir";
    CHECK_EQ(fa_engine_create(&cfg, &e), FA_OK);   /* only a cache: the engine starts */
    memset(&info, 0, sizeof(info)); info.struct_size = sizeof(info);
    CHECK_EQ(fa_engine_get_cache_info(e, &info), FA_OK);
    CHECK_EQ(info.status, FA_ERROR_IO);
    fa_engine_destroy(e);
    remove(blocker);

    cfg.cache_directory = NULL;                    /* the OS's per-user cache folder */
    CHECK_EQ(fa_engine_create(&cfg, &e), FA_OK);
    memset(&info, 0, sizeof(info)); info.struct_size = sizeof(info);
    CHECK_EQ(fa_engine_get_cache_info(e, &info), FA_OK);
    CHECK_EQ(info.is_default, 1);
    CHECK_EQ(info.status, FA_OK);
    CHECK(info.directory && strstr(info.directory, "FatzAudio"));
    fa_engine_destroy(e);

    cfg.flags |= FA_ENGINE_NO_CACHE;
    CHECK_EQ(fa_engine_create(&cfg, &e), FA_OK);
    memset(&info, 0, sizeof(info)); info.struct_size = sizeof(info);
    CHECK_EQ(fa_engine_get_cache_info(e, &info), FA_OK);
    CHECK_EQ(info.status, FA_ERROR_NOT_FOUND);
    CHECK(info.directory == NULL);
    fa_engine_destroy(e);
}

/* Two processes writing at once (two caches on one folder, spelled two ways), with
 * enough rewrites in between: no song either one wrote is lost. */
typedef struct lc_writer { fa_lcache* c; uint64_t first; } lc_writer;
static void lc_write_many(void* arg)
{
    lc_writer* w = (lc_writer*)arg;
    fa_loudness l = lc_value(-20.0);
    uint64_t i;
    int pass;
    for (pass = 0; pass < 8; pass++) {          /* each pass makes the one before stale: rewrites */
        for (i = 0; i < 400; i++) {
            l.integrated_lufs = -20.0 - pass - (double)i / 1000.0;
            fa_lcache_put(w->c, w->first + i, 10, -1, &l);
        }
    }
}

static void test_lcache_two_processes(void)
{
    lc_writer w[2];
    fa_thread t[2];
    fa_lcache* c;
    fa_loudness got;
    uint64_t i;
    int k, lost = 0, wrong = 0;
    fa_mkdir_utf8("lcache-race");
    w[0].c = fa_lcache_new("lcache-race");
    w[1].c = fa_lcache_new("lcache-race/.");
    CHECK(w[0].c && w[1].c && w[0].c != w[1].c);
    fa_lcache_clear(w[0].c);
    fa_lcache_set_limit(w[0].c, 128 * 1024);       /* room for all 800 songs, not for all passes */
    fa_lcache_set_limit(w[1].c, 128 * 1024);
    w[0].first = 1000;
    w[1].first = 5000;
    for (k = 0; k < 2; k++) CHECK_EQ(fa_thread_create(&t[k], lc_write_many, &w[k]), 0);
    for (k = 0; k < 2; k++) fa_thread_join(&t[k]);
    for (k = 0; k < 2; k++) fa_lcache_free(w[k].c);

    c = fa_lcache_new("lcache-race/./.");          /* a third process, later */
    for (k = 0; k < 2; k++) {
        for (i = 0; i < 400; i++) {
            if (!fa_lcache_get(c, w[k].first + i, 10, -1, &got)) lost++;
            else if (fabs(got.integrated_lufs - (-27.0 - (double)i / 1000.0)) > 1e-4) wrong++;
        }
    }
    CHECK_EQ(lost, 0);
    CHECK_EQ(wrong, 0);
    CHECK(file_size("lcache-race/loudness.fac") <= 128 * 1024);
    fa_lcache_clear(c);
    fa_lcache_free(c);
}

/* A damaged file is never trusted: a bad header is dropped and the file written again,
 * garbage between records is skipped. */
static void test_lcache_damaged_file(void)
{
    fa_lcache* c;
    fa_loudness l = lc_value(-8.0), got;
    uint8_t junk[4096];
    FILE* f;
    size_t i;
    uint32_t x = 12345;
    for (i = 0; i < sizeof(junk); i++) { x = x * 1103515245u + 12345u; junk[i] = (uint8_t)(x >> 16); }
    fa_mkdir_utf8("lcache-bad");

    /* Cut inside the header. */
    f = fopen("lcache-bad/loudness.fac", "wb");
    CHECK(f != NULL);
    fwrite("FatzA", 1, 5, f);
    fclose(f);
    c = fa_lcache_new("lcache-bad");
    CHECK(!fa_lcache_get(c, 1, 10, -1, &got));
    CHECK_EQ(file_size("lcache-bad/loudness.fac"), 16);   /* written again, empty */
    fa_lcache_put(c, 1, 10, -1, &l);
    fa_lcache_free(c);

    /* Junk where the header should be: another program's file, or a damaged disk. */
    f = fopen("lcache-bad/loudness.fac", "wb");
    CHECK(f != NULL);
    fwrite(junk, 1, sizeof(junk), f);
    fclose(f);
    c = fa_lcache_new("lcache-bad");
    CHECK(!fa_lcache_get(c, 1, 10, -1, &got));
    CHECK_EQ(file_size("lcache-bad/loudness.fac"), 16);
    fa_lcache_put(c, 2, 10, -1, &l);
    fa_lcache_free(c);

    /* A good header, junk, a good record, more junk with a stray marker in it. */
    f = fopen("lcache-bad/loudness.fac", "ab");
    CHECK(f != NULL);
    fwrite(junk, 1, 1000, f);
    fclose(f);
    c = fa_lcache_new("lcache-bad/.");
    l.integrated_lufs = -7.0;
    fa_lcache_put(c, 3, 10, -1, &l);
    fa_lcache_free(c);
    f = fopen("lcache-bad/loudness.fac", "ab");
    CHECK(f != NULL);
    memcpy(junk + 100, "FALR", 4);
    fwrite(junk, 1, 2000, f);
    fclose(f);
    c = fa_lcache_new("lcache-bad");
    CHECK(fa_lcache_get(c, 2, 10, -1, &got)); CHECK_NEAR(got.integrated_lufs, -8.0, 1e-6);
    CHECK(fa_lcache_get(c, 3, 10, -1, &got)); CHECK_NEAR(got.integrated_lufs, -7.0, 1e-6);
    CHECK_EQ(file_size("lcache-bad/loudness.fac"), 16 + 2 * 48);   /* the junk is gone */
    fa_lcache_clear(c);
    fa_lcache_free(c);
}

/* A full 5 MB cache written as the format says, then read: how long the first lookup takes. */
static void put32le(uint8_t* p, uint32_t v) { int i; for (i = 0; i < 4; i++) p[i] = (uint8_t)(v >> (i * 8)); }
static void test_lcache_load_time(void)
{
    const uint32_t n = (5u * 1024u * 1024u - 16u) / 48u;
    uint8_t h[16], r[48];
    uint32_t i;
    float v;
    fa_lcache* c;
    fa_loudness got;
    int64_t t0;
    double first_ms;
    FILE* f;
    fa_mkdir_utf8("lcache-big");
    f = fopen("lcache-big/loudness.fac", "wb");
    CHECK(f != NULL);
    memset(h, 0, sizeof(h));
    memcpy(h, "FatzAudio-LC", 12);
    put32le(h + 12, 3);
    fwrite(h, 1, 16, f);
    for (i = 0; i < n; i++) {
        uint64_t key = 0x9E3779B97F4A7C15ULL * (i + 1);
        memset(r, 0, sizeof(r));
        memcpy(r, "FALR", 4);
        put32le(r + 4, 29000000u + i / 100);
        put32le(r + 8, (uint32_t)key); put32le(r + 12, (uint32_t)(key >> 32));
        put32le(r + 16, 4000000u + i);
        put32le(r + 24, 0xFFFFFFFFu);
        v = -14.0f;
        { uint32_t b; memcpy(&b, &v, 4); put32le(r + 28, b); }
        put32le(r + 44, (uint32_t)fa_fnv1a(0xcbf29ce484222325ULL, r, 44));
        fwrite(r, 1, 48, f);
    }
    fclose(f);
    c = fa_lcache_new("lcache-big");
    t0 = fa_monotonic_ns();
    CHECK(fa_lcache_get(c, 0x9E3779B97F4A7C15ULL * 1000, 4000000 + 999, -1, &got));
    first_ms = (double)(fa_monotonic_ns() - t0) / 1e6;
    CHECK_NEAR(got.integrated_lufs, -14.0, 1e-6);
    t0 = fa_monotonic_ns();
    CHECK(fa_lcache_get(c, 0x9E3779B97F4A7C15ULL * 2000, 4000000 + 1999, -1, &got));
    printf("  5 MB cache, %u songs: first lookup (reads the file) %.1f ms, the next %.3f ms\n", n, first_ms,
           (double)(fa_monotonic_ns() - t0) / 1e6);
    CHECK(first_ms < 2000.0);
    fa_lcache_clear(c);
    fa_lcache_free(c);
}

/* ---- content keys ------------------------------------------------------------------- */

static int key_of(const uint8_t* p, size_t n, uint64_t* key, int64_t* size)
{
    fa_source src;
    int ok;
    if (fa_source_open_memory_ref(&src, p, n) != FA_OK) return 0;
    ok = fa_content_key(&src, key, size);
    fa_source_close(&src);
    return ok;
}

/* Fake coded audio: bytes that change everywhere with `seed`. */
static void fill_audio(uint8_t* p, size_t n, uint32_t seed)
{
    size_t i;
    for (i = 0; i < n; i++) { seed = seed * 1664525u + 1013904223u; p[i] = (uint8_t)(seed >> 24); }
}

static void test_key_ignores_tags(void)
{
    enum { AUDIO = 300000 };
    uint8_t* a = (uint8_t*)malloc(AUDIO + 4096);
    uint8_t* b = (uint8_t*)malloc(AUDIO + 4096);
    uint64_t ka, kb;
    int64_t sa, sb;
    size_t na, nb;
    CHECK(a && b);
    /* MP3: ID3v2 (10 + 20 bytes) + audio + ID3v1, retagged as ID3v2 (10 + 200) + audio + APEv2 + ID3v1. */
    memcpy(a, "ID3\4\0\0\0\0\0\x14", 10);
    memset(a + 10, 'x', 20);
    fill_audio(a + 30, AUDIO, 7);
    memcpy(a + 30 + AUDIO, "TAG", 3);
    memset(a + 33 + AUDIO, 'y', 125);
    na = 30 + AUDIO + 128;
    memcpy(b, "ID3\4\0\0\0\0\x01\x48", 10);      /* 200 = 1 << 7 | 72 */
    memset(b + 10, 'z', 200);
    fill_audio(b + 210, AUDIO, 7);
    memset(b + 210 + AUDIO, 'i', 40);                /* APEv2 items, then its 32-byte footer */
    memcpy(b + 250 + AUDIO, "APETAGEX", 8);
    put32le(b + 258 + AUDIO, 2000);
    put32le(b + 262 + AUDIO, 72);                    /* items + footer */
    put32le(b + 266 + AUDIO, 1);
    put32le(b + 270 + AUDIO, 0);
    memset(b + 274 + AUDIO, 0, 8);
    memcpy(b + 282 + AUDIO, "TAG", 3);
    memset(b + 285 + AUDIO, 'q', 125);
    nb = 282 + AUDIO + 128;
    CHECK(key_of(a, na, &ka, &sa));
    CHECK(key_of(b, nb, &kb, &sb));
    CHECK_EQ(sa, AUDIO);
    CHECK_EQ(sb, AUDIO);
    CHECK(ka == kb);                                 /* retagged: the same song */
    fill_audio(b + 210, AUDIO, 8);                   /* encoded again: another song */
    CHECK(key_of(b, nb, &kb, &sb));
    CHECK(ka != kb);

    /* FLAC: a bigger comment block does not matter. */
    memcpy(a, "fLaC\x80\0\0\x08", 8);
    memset(a + 8, 1, 8);
    fill_audio(a + 16, AUDIO, 9);
    memcpy(b, "fLaC\x04\0\0\x40", 8);
    memset(b + 8, 2, 64);
    memcpy(b + 72, "\x81\0\0\x08", 4);
    memset(b + 76, 3, 8);
    fill_audio(b + 84, AUDIO, 9);
    CHECK(key_of(a, 16 + AUDIO, &ka, &sa));
    CHECK(key_of(b, 84 + AUDIO, &kb, &sb));
    CHECK(ka == kb);
    free(a);
    free(b);
}

static void test_key_hashes_all_of_a_wav(void)
{
    /* A WAV turned down in the middle, same length, the 16 sample points untouched:
     * a new key (the samples are hashed whole). A new LIST chunk: the same key. */
    int64_t n, sa, sb, sc;
    size_t size, i;
    segment seg = { 30.0, -10.0, 1 };
    float* x = make_signal(44100, 2, &seg, 1, 440.0, &n);
    uint8_t* wav = make_wav(44100, 2, x, n, NULL, 0, &size);
    static const uint8_t list[] = { 'L', 'I', 'S', 'T', 12, 0, 0, 0, 'I', 'N', 'F', 'O', 'I', 'N', 'A', 'M', 2, 0, 0, 0, 'x', 0 };
    uint8_t* tagged = (uint8_t*)malloc(size + sizeof(list));
    uint64_t ka, kb, kc;
    free(x);
    CHECK(tagged != NULL);
    CHECK(key_of(wav, size, &ka, &sa));
    memcpy(tagged, wav, size);
    memcpy(tagged + size, list, sizeof(list));
    put32le(tagged + 4, (uint32_t)(size + sizeof(list) - 8));
    CHECK(key_of(tagged, size + sizeof(list), &kb, &sb));
    CHECK(ka == kb);
    CHECK_EQ(sa, sb);
    for (i = size / 2 + 100000; i < size / 2 + 100200; i++) wav[i] ^= 0x10;   /* between two sample points */
    CHECK(key_of(wav, size, &kc, &sc));
    CHECK(kc != ka);
    free(wav);
    free(tagged);
}

static void test_key_of_a_big_wav(void)
{
    /* Over 64 MB the samples are not read whole: 64 points (and, for a file, its time).
     * The key is still stable and still ignores a LIST chunk. */
    const size_t data = (size_t)65 << 20;
    uint8_t* w = (uint8_t*)calloc(1, 44 + data + 22);
    static const uint8_t list[] = { 'L', 'I', 'S', 'T', 12, 0, 0, 0, 'I', 'N', 'F', 'O', 'I', 'N', 'A', 'M', 2, 0, 0, 0, 'x', 0 };
    uint64_t ka, kb;
    int64_t sa, sb, t0;
    double ms;
    CHECK(w != NULL);
    memcpy(w, "RIFF\0\0\0\0WAVEfmt \x10\0\0\0\1\0\2\0\x44\xac\0\0\x10\xb1\2\0\4\0\x10\0data", 40);
    put32le(w + 40, (uint32_t)data);
    fill_audio(w + 44, data, 5);
    put32le(w + 4, (uint32_t)(36 + data));
    t0 = fa_monotonic_ns();
    CHECK(key_of(w, 44 + data, &ka, &sa));
    ms = (double)(fa_monotonic_ns() - t0) / 1e6;
    printf("  key of a 65 MB WAV: %.2f ms\n", ms);
    CHECK_EQ(sa, (int64_t)data);
    memcpy(w + 44 + data, list, sizeof(list));
    put32le(w + 4, (uint32_t)(36 + data + sizeof(list)));
    CHECK(key_of(w, 44 + data + sizeof(list), &kb, &sb));
    CHECK(ka == kb);
    free(w);
}

static void test_cache_api(void)
{
    CHECK_EQ(fa_engine_set_cache_size(g_engine, 0), FA_OK);            /* no cache directory: nothing to do */
    CHECK_EQ(fa_engine_set_cache_size(g_engine, 1000), FA_ERROR_INVALID_ARG);
    CHECK_EQ(fa_engine_clear_cache(g_engine), FA_OK);
    CHECK_EQ(fa_engine_clear_cache(NULL), FA_ERROR_INVALID_ARG);
}

/* ---- start wait and provisional values ----------------------------------------------- */

static void test_spread_order(void)
{
    int n, i;
    for (n = 1; n <= 40; n++) {
        int seen[40] = { 0 };
        for (i = 0; i < n; i++) {
            int k = fa_scan_spread_chunk(i, n);
            CHECK(k >= 0 && k < n);
            CHECK(!seen[k]);
            seen[k] = 1;
        }
    }
    /* The first chunks taken lie all over the file. */
    CHECK_EQ(fa_scan_spread_chunk(0, 8), 0);
    CHECK_EQ(fa_scan_spread_chunk(1, 8), 4);
    CHECK_EQ(fa_scan_spread_chunk(2, 8), 2);
    CHECK_EQ(fa_scan_spread_chunk(3, 8), 6);
}

static void test_partial_from_finished_chunks(void)
{
    /* A stream's scan hands over the loudness of the chunks finished so far before
     * the final value; with 7 of 8 chunks it is already close to the final one. */
    int64_t n;
    size_t size;
    float* x = make_signal(44100, 2, g_song, 6, 440.0, &n);
    uint8_t* wav = make_wav(44100, 2, x, n, NULL, 0, &size);
    fa_stream* s = NULL;
    fa_loudness l = { .struct_size = sizeof(l) };
    float partial;
    free(x);
    CHECK_EQ(fa_engine_set_normalization(g_engine, FA_NORMALIZE_TRACK, -23.0), FA_OK);
    CHECK_EQ(fa_stream_open_memory(g_engine, wav, size, NULL, &s), FA_OK);
    CHECK(wait_final(s, &l));
    CHECK_EQ(l.source, FA_LOUDNESS_SCAN);
    partial = fa_load_float(&s->norm_partial);
    CHECK(partial == partial);
    CHECK_NEAR(partial, l.integrated_lufs, 3.0);
    CHECK_EQ(fa_load32(&s->norm_pending), 0);
    fa_stream_close(s);
    free(wav);
}

typedef struct prov_events { fa_atomic32 count, source, finals; } prov_events;
static void FA_CALL prov_cb(const fa_event* ev, void* user)
{
    prov_events* p = (prov_events*)user;
    if (ev->type == FA_EVENT_LOUDNESS_PROVISIONAL) {
        fa_store32(&p->source, (int32_t)ev->value);
        fa_fetch_add32(&p->count, 1);
    } else if (ev->type == FA_EVENT_LOUDNESS_FINAL) {
        fa_fetch_add32(&p->finals, 1);
    }
}

/* A stream whose scan is still running, made by hand so the test is not timing
 * dependent: the measured value is put back as unknown and the scan as pending. */
static fa_stream* open_unmeasured(double dbfs, fa_loudness* measured, prov_events* ev)
{
    fa_stream* s = open_sine(20.0, dbfs, 0, NULL, 0);
    if (!s || !wait_final(s, measured)) return NULL;
    fa_mutex_lock(&s->norm_lock);
    s->loud.is_final = 0;
    fa_store_float(&s->norm_track, NAN);
    fa_store_float(&s->norm_est, NAN);
    fa_store32(&s->norm_pending, 1);
    fa_store32(&s->norm_progress, 0);
    fa_mutex_unlock(&s->norm_lock);
    memset(ev, 0, sizeof(*ev));
    fa_stream_set_event_callback(s, prov_cb, ev);
    return s;
}

static int wait_count(fa_atomic32* c, int32_t want)
{
    int i;
    for (i = 0; i < 1000 && fa_load32(c) < want; i++) fa_sleep_ms(2);
    return fa_load32(c) >= want;
}

static void test_start_wait_holds_until_final(void)
{
    fa_stream* s;
    fa_loudness m = { .struct_size = sizeof(m) };
    prov_events ev;
    fa_frames pos = -1;
    float out[2 * 480];
    int i;
    CHECK_EQ(fa_engine_set_normalization(g_engine, FA_NORMALIZE_TRACK, -23.0), FA_OK);
    CHECK_EQ(fa_engine_set_normalization_start_wait(g_engine, 5000, 12.0), FA_OK);
    s = open_unmeasured(-10.0, &m, &ev);
    CHECK(s != NULL);
    CHECK_EQ(fa_stream_attach(s, g_dev, NULL), FA_OK);
    CHECK_EQ(fa_stream_play(s), FA_OK);
    for (i = 0; i < 5; i++) {
        render(s, out, 480);
        CHECK(peak_of(out, 960) == 0.0);          /* held: nothing heard */
    }
    CHECK_EQ(fa_stream_get_position(s, &pos), FA_OK);
    CHECK_EQ(pos, 0);                             /* the position and the clock wait too */
    CHECK_EQ(fa_load64(&s->clock.count), 0);
    {
        fa_time t = -1;
        CHECK_EQ(fa_stream_get_clock(s, &t), FA_OK);
        CHECK_EQ(t, 0);
    }
    {
        fa_stream_state st;
        CHECK_EQ(fa_stream_get_state(s, &st), FA_OK);
        CHECK_EQ(st, FA_STATE_PLAYING);
    }
    fa_stream_loudness_ready(s, &m, NAN);         /* the scan finishes */
    render(s, out, 480);
    /* Starts at once, at the exact gain from the first sample. */
    CHECK_NEAR(peak_of(out, 960), pow(10.0, (-10.0 + (-23.0 - m.integrated_lufs)) / 20.0), 2e-3);
    CHECK(wait_count(&ev.finals, 1));
    fa_sleep_ms(20);
    CHECK_EQ(fa_load32(&ev.count), 0);           /* started on a final value */
    fa_stream_close(s);
    CHECK_EQ(fa_engine_set_normalization_start_wait(g_engine, 200, 12.0), FA_OK);
}

static void test_start_wait_runs_out(void)
{
    /* The scan is not done in time: the stream starts on the partial value, boosted by
     * at most the provisional cap, says so, and glides once the value is final. */
    fa_stream* s;
    fa_loudness m = { .struct_size = sizeof(m) }, now = { .struct_size = sizeof(now) };
    prov_events ev;
    float out[2 * 480];
    int64_t t0;
    double first_db;
    int i;
    CHECK_EQ(fa_engine_set_normalization(g_engine, FA_NORMALIZE_TRACK, -23.0), FA_OK);
    CHECK_EQ(fa_engine_set_normalization_start_wait(g_engine, 60, 4.0), FA_OK);
    s = open_unmeasured(-30.0, &m, &ev);          /* -33 LUFS: wants +10 dB */
    CHECK(s != NULL);
    fa_stream_loudness_partial(s, m.integrated_lufs);
    CHECK_EQ(fa_stream_get_loudness(s, &now), FA_OK);
    CHECK_EQ(now.source, FA_LOUDNESS_PARTIAL);
    CHECK_EQ(now.is_final, 0);
    CHECK_NEAR(now.gain_db, 4.0, 1e-4);           /* capped while provisional */
    CHECK_EQ(fa_stream_attach(s, g_dev, NULL), FA_OK);
    CHECK_EQ(fa_stream_play(s), FA_OK);
    t0 = fa_monotonic_ns();
    render(s, out, 480);
    CHECK(peak_of(out, 960) == 0.0);
    while (fa_monotonic_ns() - t0 < 70000000) fa_sleep_ms(5);
    render(s, out, 480);
    first_db = 20.0 * log10(peak_of(out, 960));
    CHECK_NEAR(first_db, -30.0 + 4.0, 0.05);
    CHECK(wait_count(&ev.count, 1));
    CHECK_EQ(fa_load32(&ev.source), FA_LOUDNESS_PARTIAL);
    fa_stream_loudness_ready(s, &m, NAN);
    for (i = 0; i < 150; i++) render(s, out, 480);   /* 1.5 s at 48 kHz: +6 dB at 6 dB/s */
    CHECK_NEAR(20.0 * log10(peak_of(out, 960)), -30.0 + (-23.0 - m.integrated_lufs), 0.05);
    CHECK_EQ(fa_load32(&ev.count), 1);
    fa_stream_close(s);
    CHECK_EQ(fa_engine_set_normalization_start_wait(g_engine, 200, 12.0), FA_OK);
    CHECK_EQ(fa_engine_set_normalization_start_wait(g_engine, 10001, 12.0), FA_ERROR_INVALID_ARG);
    CHECK_EQ(fa_engine_set_normalization_start_wait(g_engine, 0, 12.5), FA_ERROR_INVALID_ARG);
    CHECK_EQ(fa_engine_set_normalization_start_wait(g_engine, 0, -1.0), FA_ERROR_INVALID_ARG);
}

static void test_start_wait_nearly_done_waits_longer(void)
{
    /* More than 75% scanned when the wait runs out: it waits on for the final value, at
     * most once more the wait, and then starts on the partial value. */
    fa_stream* s;
    fa_loudness m = { .struct_size = sizeof(m) };
    prov_events ev;
    float out[2 * 480];
    int64_t t0;
    CHECK_EQ(fa_engine_set_normalization(g_engine, FA_NORMALIZE_TRACK, -23.0), FA_OK);
    CHECK_EQ(fa_engine_set_normalization_start_wait(g_engine, 60, 12.0), FA_OK);
    s = open_unmeasured(-10.0, &m, &ev);
    CHECK(s != NULL);
    fa_stream_loudness_partial(s, m.integrated_lufs);
    fa_store32(&s->norm_progress, 800);
    CHECK_EQ(fa_stream_attach(s, g_dev, NULL), FA_OK);
    CHECK_EQ(fa_stream_play(s), FA_OK);
    t0 = fa_monotonic_ns();
    render(s, out, 480);
    CHECK(peak_of(out, 960) == 0.0);
    while (fa_monotonic_ns() - t0 < 80000000) fa_sleep_ms(5);
    render(s, out, 480);
    if (fa_monotonic_ns() - t0 < 115000000) CHECK(peak_of(out, 960) == 0.0);   /* still waiting at 80 ms */
    while (fa_monotonic_ns() - t0 < 130000000) fa_sleep_ms(5);
    render(s, out, 480);
    CHECK(peak_of(out, 960) > 0.0);                 /* gave up at 120 ms */
    CHECK(wait_count(&ev.count, 1));
    CHECK_EQ(fa_load32(&ev.source), FA_LOUDNESS_PARTIAL);
    fa_stream_close(s);

    /* At 75% or less it starts when the wait runs out. */
    s = open_unmeasured(-10.0, &m, &ev);
    CHECK(s != NULL);
    fa_store32(&s->norm_progress, 750);
    CHECK_EQ(fa_stream_attach(s, g_dev, NULL), FA_OK);
    CHECK_EQ(fa_stream_play(s), FA_OK);
    t0 = fa_monotonic_ns();
    render(s, out, 480);
    while (fa_monotonic_ns() - t0 < 70000000) fa_sleep_ms(5);
    render(s, out, 480);
    CHECK(peak_of(out, 960) > 0.0);
    fa_stream_close(s);
    CHECK_EQ(fa_engine_set_normalization_start_wait(g_engine, 200, 12.0), FA_OK);
}

static void test_scheduled_start_never_waits(void)
{
    fa_stream* s;
    fa_loudness m = { .struct_size = sizeof(m) };
    prov_events ev;
    float out[2 * 480];
    int64_t clock;
    CHECK_EQ(fa_engine_set_normalization(g_engine, FA_NORMALIZE_TRACK, -23.0), FA_OK);
    CHECK_EQ(fa_engine_set_normalization_start_wait(g_engine, 5000, 12.0), FA_OK);
    s = open_unmeasured(-10.0, &m, &ev);
    CHECK(s != NULL);
    CHECK_EQ(fa_stream_attach(s, g_dev, NULL), FA_OK);
    clock = fa_load64(&g_dev->clock);
    fa_mutex_lock(&s->api_lock);
    fa_store64(&s->start_frame, clock);           /* due in the next callback */
    fa_store32(&s->state, FA_STATE_SCHEDULED);
    fa_mutex_unlock(&s->api_lock);
    render(s, out, 480);
    CHECK(peak_of(out, 960) > 0.0);
    CHECK(wait_count(&ev.count, 1));
    CHECK(fa_load32(&ev.source) == FA_LOUDNESS_NONE || fa_load32(&ev.source) == FA_LOUDNESS_ESTIMATE);
    fa_stream_close(s);
    CHECK_EQ(fa_engine_set_normalization_start_wait(g_engine, 200, 12.0), FA_OK);
}

/* ---- scans of fed audio (MediaControl request 13) ----------------------------------- */

static fa_engine* fed_engine(const char* dir)
{
    fa_engine_config cfg;
    fa_engine* e = NULL;
    fa_engine_config_init(&cfg);
    cfg.flags = FA_ENGINE_NULL_OUTPUT;
    cfg.worker_threads = 2;
    cfg.cache_directory = dir;
    if (fa_engine_create(&cfg, &e) != FA_OK) return NULL;
    return e;
}

/* Feeds x (stereo F32 at `rate`) as PCM packets, blocks of 4096 frames, the first `part`
 * of the frames only (0 = all). */
static fa_result feed_packets(fa_scan_feed* f, const float* x, int64_t frames, int64_t part)
{
    int64_t at = 0, end = part ? part : frames;
    while (at < end) {
        int64_t n = end - at < 4096 ? end - at : 4096;
        fa_packet p;
        fa_result r;
        memset(&p, 0, sizeof(p));
        p.data = x + at * 2;
        p.size = (size_t)n * 2 * sizeof(float);
        p.pts = FA_PTS_NONE;
        r = fa_scan_push_packet(f, &p);
        if (r != FA_OK) return r;
        at += n;
    }
    return FA_OK;
}

static fa_result feed_frames(fa_scan_feed* f, const float* x, int64_t frames, uint32_t rate, int64_t part)
{
    int64_t at = 0, end = part ? part : frames;
    while (at < end) {
        int64_t n = end - at < 4096 ? end - at : 4096;
        const void* planes[1];
        fa_audio_frame fr;
        fa_result r;
        planes[0] = x + at * 2;
        memset(&fr, 0, sizeof(fr));
        fr.format.sample_format = FA_FORMAT_F32;
        fr.format.sample_rate = rate;
        fr.format.channels = 2;
        fr.data = planes;
        fr.frames = n;
        fr.pts = FA_PTS_NONE;
        r = fa_scan_push_frame(f, &fr);
        if (r != FA_OK) return r;
        at += n;
    }
    return FA_OK;
}

static void fed_params(fa_codec_params* cp, uint32_t rate)
{
    fa_codec_params_init(cp);
    cp->codec = "pcm_f32le";
    cp->sample_rate = rate;
    cp->channels = 2;
}

static void test_fed_scan_matches_path_scan(void)
{
    uint32_t rate = 44100;
    int64_t n;
    size_t size;
    float* x = make_signal(rate, 2, g_song, 6, 440.0, &n);
    uint8_t* wav = make_wav(rate, 2, x, n, NULL, 0, &size);
    fa_loudness path = { .struct_size = sizeof(path) }, a = { .struct_size = sizeof(a) }, b = { .struct_size = sizeof(b) };
    fa_fingerprint id = { .struct_size = sizeof(id) };
    fa_scan_options so;
    fa_codec_params cp;
    fa_audio_format af;
    fa_scan_feed *f1 = NULL, *f2 = NULL;
    int64_t t0;
    CHECK(write_file("loudness-fed.wav", wav, size));
    free(wav);
    CHECK_EQ(fa_scan_loudness_path(g_engine, "loudness-fed.wav", &path), FA_OK);
    CHECK_EQ(fa_fingerprint_path("loudness-fed.wav", &id), FA_OK);
    fa_scan_options_init(&so);
    so.identity = &id;

    fed_params(&cp, rate);
    CHECK_EQ(fa_scan_open_packets(g_engine, &cp, &so, &f1), FA_OK);
    CHECK_EQ(fa_scan_wants_more(f1), 1);
    t0 = fa_monotonic_ns();
    CHECK_EQ(feed_packets(f1, x, n, 0), FA_OK);
    CHECK_EQ(fa_scan_finish(f1, &a), FA_OK);
    printf("  fed scan of %.0f s of audio on one thread: %.1f ms\n", (double)n / rate, (double)(fa_monotonic_ns() - t0) / 1e6);
    CHECK_EQ(a.source, FA_LOUDNESS_SCAN);
    CHECK_EQ(a.is_final, 1);
    CHECK_NEAR(a.integrated_lufs, path.integrated_lufs, 0.02);
    CHECK_NEAR(a.range_lu, path.range_lu, 0.1);
    CHECK_NEAR(a.true_peak_dbtp, path.true_peak_dbtp, 0.01);
    CHECK_EQ(fa_scan_finish(f1, &b), FA_OK);     /* again: the same result */
    CHECK(a.integrated_lufs == b.integrated_lufs);
    CHECK_EQ(fa_scan_push_packet(f1, &(fa_packet){ x, 8, 0, 0 }), FA_ERROR_INVALID_ARG);   /* after the end */
    fa_scan_close(f1);

    memset(&af, 0, sizeof(af));
    af.sample_rate = rate;
    af.channels = 2;
    af.sample_format = FA_FORMAT_F32;
    CHECK_EQ(fa_scan_open_push(g_engine, &af, &so, &f2), FA_OK);
    CHECK_EQ(feed_frames(f2, x, n, rate, 0), FA_OK);
    CHECK_EQ(fa_scan_finish(f2, &b), FA_OK);
    CHECK(b.integrated_lufs == a.integrated_lufs);   /* decoded frames or packets: the same numbers */
    CHECK(b.true_peak_dbtp == a.true_peak_dbtp);
    /* A frame in another rate, a packet into a frame feed: refused. */
    CHECK_EQ(feed_frames(f2, x, 4096, 48000, 0), FA_ERROR_INVALID_ARG);   /* after the end */
    fa_scan_close(f2);
    free(x);
}

static void test_fed_scan_errors(void)
{
    fa_loudness l = { .struct_size = sizeof(l) };
    fa_fingerprint id = { .struct_size = sizeof(id), .key = 1, .audio_bytes = 2 };
    fa_scan_options so;
    fa_codec_params cp;
    fa_audio_format af;
    fa_scan_feed* f = NULL;
    float x[8] = { 0 };
    fa_scan_options_init(&so);
    fed_params(&cp, 44100);
    CHECK_EQ(fa_scan_open_packets(g_engine, &cp, &so, &f), FA_ERROR_INVALID_ARG);   /* no identity */
    CHECK(f == NULL);
    so.identity = &id;
    cp.codec = "not_a_codec";
    CHECK_EQ(fa_scan_open_packets(g_engine, &cp, &so, &f), FA_ERROR_UNSUPPORTED_FORMAT);
    fed_params(&cp, 44100);
    so.duration_seconds = -1.0;
    CHECK_EQ(fa_scan_open_packets(g_engine, &cp, &so, &f), FA_ERROR_INVALID_ARG);
    so.duration_seconds = 0.0;
    CHECK_EQ(fa_scan_open_packets(g_engine, &cp, &so, &f), FA_OK);
    CHECK_EQ(fa_scan_finish(f, &l), FA_ERROR_END_OF_STREAM);                         /* nothing fed */
    CHECK_EQ(fa_scan_push_frame(f, NULL), FA_ERROR_INVALID_ARG);
    CHECK_EQ(feed_frames(f, x, 4, 44100, 0), FA_ERROR_INVALID_ARG);                 /* finished */
    fa_scan_close(f);
    memset(&af, 0, sizeof(af));
    af.sample_rate = 44100;
    af.channels = 2;
    af.sample_format = FA_FORMAT_F32;
    CHECK_EQ(fa_scan_open_push(g_engine, &af, &so, &f), FA_OK);
    CHECK_EQ(feed_frames(f, x, 4, 48000, 0), FA_ERROR_UNSUPPORTED_FORMAT);
    {
        fa_packet p = { x, 8, 0, 0 };
        CHECK_EQ(fa_scan_push_packet(f, &p), FA_ERROR_INVALID_ARG);                  /* a frame feed */
    }
    fa_scan_close(f);
    fa_scan_close(NULL);
    CHECK_EQ(fa_scan_wants_more(NULL), 0);
}

/* The stream's analysis is a job on a scan thread: it has looked the song up when pending clears. */
static int wait_not_pending(fa_stream* s)
{
    int i;
    for (i = 0; i < 2000 && fa_load32(&s->norm_pending); i++) fa_sleep_ms(2);
    return !fa_load32(&s->norm_pending);
}

static void fed_clean(const char* dir)
{
    char path[256];
    snprintf(path, sizeof(path), "%s/loudness.fac", dir);
    remove(path);
    snprintf(path, sizeof(path), "%s/loudness.fac.lock", dir);
    remove(path);
}

static fa_stream* open_fed_stream(fa_engine* e, uint32_t rate, const fa_fingerprint* id)
{
    fa_open_options o;
    fa_audio_format af;
    fa_stream* s = NULL;
    memset(&af, 0, sizeof(af));
    af.sample_rate = rate;
    af.channels = 2;
    af.sample_format = FA_FORMAT_F32;
    fa_open_options_init(&o);
    o.source_identity = id;
    if (fa_stream_open_push(e, &af, &o, &s) != FA_OK) return NULL;
    return s;
}

static int wait_final_in(fa_stream* s, fa_loudness* l)
{
    int i;
    for (i = 0; i < 2000; i++) {
        fa_stream_get_loudness(s, l);
        if (l->is_final) return 1;
        fa_sleep_ms(2);
    }
    return 0;
}

/* The scan, the cache and the playing stream of one song, whichever starts first. */
static void test_fed_scan_reaches_streams_and_cache(void)
{
    uint32_t rate = 44100;
    int64_t n;
    float* x = make_signal(rate, 2, g_song, 6, 440.0, &n);
    fa_engine* e;
    fa_loudness first = { .struct_size = sizeof(first) }, l = { .struct_size = sizeof(l) }, c = { .struct_size = sizeof(c) };
    fa_fingerprint id = { .struct_size = sizeof(id), .key = 0x1234abcdULL, .audio_bytes = 777 };
    fa_scan_options so;
    fa_codec_params cp;
    fa_scan_feed *f = NULL, *f2 = NULL;
    fa_stream *s, *s2;
    prov_events ev;
    float partial;
    fed_clean("fed-cache");
    e = fed_engine("fed-cache");
    CHECK(e != NULL);
    CHECK_EQ(fa_engine_set_normalization(e, FA_NORMALIZE_TRACK, -23.0), FA_OK);
    fa_scan_options_init(&so);
    so.identity = &id;
    so.duration_seconds = (double)n / rate;
    fed_params(&cp, rate);

    /* 1. The stream first: it waits for the feed, sees its progress, gets its result. */
    s = open_fed_stream(e, rate, &id);
    CHECK(s != NULL);
    memset(&ev, 0, sizeof(ev));
    fa_stream_set_event_callback(s, prov_cb, &ev);
    CHECK_EQ(fa_stream_get_loudness(s, &l), FA_OK);
    CHECK_EQ(l.is_final, 0);
    CHECK(wait_not_pending(s));                               /* no feed yet: nothing to wait for */
    CHECK_EQ(fa_scan_open_packets(e, &cp, &so, &f), FA_OK);
    CHECK_EQ(fa_load32(&s->norm_pending), 1);                 /* the feed started: it waits for it */
    CHECK_EQ(feed_packets(f, x, n, n * 6 / 10), FA_OK);
    CHECK(fa_load32(&s->norm_progress) > 500 && fa_load32(&s->norm_progress) < 700);
    partial = fa_load_float(&s->norm_partial);
    CHECK(partial == partial);                                 /* past a quarter: a value so far */
    fa_stream_get_loudness(s, &l);
    CHECK_EQ(l.is_final, 0);
    CHECK_EQ(l.source, FA_LOUDNESS_PARTIAL);
    CHECK_EQ(feed_packets(f, x + (n * 6 / 10) * 2, n - n * 6 / 10, 0), FA_OK);
    CHECK_EQ(fa_scan_finish(f, &first), FA_OK);
    CHECK(wait_final_in(s, &l));
    CHECK_EQ(l.source, FA_LOUDNESS_SCAN);
    CHECK_NEAR(l.integrated_lufs, first.integrated_lufs, 1e-6);
    CHECK_EQ(fa_load32(&s->norm_pending), 0);
    CHECK(wait_count(&ev.finals, 1));
    fa_scan_close(f);

    /* 2. The song is in the cache: a feed has nothing to do, a new stream starts final. */
    CHECK_EQ(fa_scan_open_packets(e, &cp, &so, &f2), FA_OK);
    CHECK_EQ(fa_scan_wants_more(f2), 0);
    CHECK_EQ(fa_scan_finish(f2, &c), FA_OK);
    CHECK_EQ(c.source, FA_LOUDNESS_CACHE);
    CHECK_NEAR(c.integrated_lufs, first.integrated_lufs, 1e-6);
    CHECK_NEAR(c.true_peak_dbtp, first.true_peak_dbtp, 1e-6);
    fa_scan_close(f2);
    s2 = open_fed_stream(e, rate, &id);
    CHECK(s2 != NULL);
    CHECK_EQ(fa_stream_get_loudness(s2, &l), FA_OK);
    CHECK(wait_final_in(s2, &l));
    CHECK_EQ(l.source, FA_LOUDNESS_CACHE);
    CHECK_NEAR(l.gain_db, -23.0 - first.integrated_lufs, 0.01);
    CHECK_EQ(fa_load32(&s2->norm_pending), 0);
    {
        /* Another track of the same file is another song. */
        fa_open_options o;
        fa_audio_format af;
        fa_stream* s3 = NULL;
        memset(&af, 0, sizeof(af));
        af.sample_rate = rate; af.channels = 2; af.sample_format = FA_FORMAT_F32;
        fa_open_options_init(&o);
        o.source_identity = &id;
        o.audio_track = 1;
        CHECK_EQ(fa_stream_open_push(e, &af, &o, &s3), FA_OK);
        fa_stream_get_loudness(s3, &l);
        CHECK_EQ(l.is_final, 0);
        fa_stream_close(s3);
    }
    fa_stream_close(s2);
    fa_stream_close(s);

    /* 3. Closing a feed early frees the stream from waiting; closing a stream during a feed is safe. */
    id.key = 0x99ULL;
    s = open_fed_stream(e, rate, &id);
    CHECK(s != NULL);
    fa_stream_get_loudness(s, &l);
    CHECK(wait_not_pending(s));
    CHECK_EQ(fa_scan_open_packets(e, &cp, &so, &f), FA_OK);
    CHECK_EQ(fa_load32(&s->norm_pending), 1);
    CHECK_EQ(feed_packets(f, x, n, n / 3), FA_OK);
    fa_scan_close(f);
    CHECK_EQ(fa_load32(&s->norm_pending), 0);
    fa_stream_get_loudness(s, &l);
    CHECK_EQ(l.is_final, 0);
    CHECK_EQ(fa_scan_open_packets(e, &cp, &so, &f), FA_OK);
    CHECK_EQ(feed_packets(f, x, n, n / 3), FA_OK);
    fa_stream_close(s);                                        /* the feed goes on without it */
    CHECK_EQ(feed_packets(f, x + (n / 3) * 2, n / 3, 0), FA_OK);
    CHECK_EQ(fa_scan_finish(f, &l), FA_OK);
    fa_scan_close(f);

    /* 4. A path scan and a fed scan share an entry: the feed's identity is the file's fingerprint. */
    {
        fa_fingerprint fp = { .struct_size = sizeof(fp) };
        fa_loudness viaf = { .struct_size = sizeof(viaf) };
        CHECK_EQ(fa_fingerprint_path("loudness-fed.wav", &fp), FA_OK);
        so.identity = &fp;
        so.duration_seconds = 0.0;
        CHECK_EQ(fa_scan_open_packets(e, &cp, &so, &f), FA_OK);
        CHECK_EQ(feed_packets(f, x, n, 0), FA_OK);
        CHECK_EQ(fa_scan_finish(f, &first), FA_OK);
        fa_scan_close(f);
        CHECK_EQ(fa_scan_loudness_path(e, "loudness-fed.wav", &viaf), FA_OK);
        CHECK_EQ(viaf.source, FA_LOUDNESS_CACHE);
        CHECK_NEAR(viaf.integrated_lufs, first.integrated_lufs, 1e-6);
    }
    /* Turning normalization on starts the analysis of open streams under the engine's list lock: a cached
     * song must still be delivered (by a scan thread), not on that thread. */
    {
        fa_fingerprint fp = { .struct_size = sizeof(fp) };
        fa_stream* s4;
        CHECK_EQ(fa_fingerprint_path("loudness-fed.wav", &fp), FA_OK);
        CHECK_EQ(fa_engine_set_normalization(e, FA_NORMALIZE_OFF, -23.0), FA_OK);
        s4 = open_fed_stream(e, rate, &fp);
        CHECK(s4 != NULL);
        CHECK_EQ(fa_engine_set_normalization(e, FA_NORMALIZE_TRACK, -23.0), FA_OK);
        CHECK(wait_final_in(s4, &l));
        CHECK_EQ(l.source, FA_LOUDNESS_CACHE);
        fa_stream_close(s4);
    }
    fa_engine_destroy(e);
    free(x);
}

/* No cache directory: a fed scan still gives its result, and nothing is stored. */
static void test_fed_scan_without_cache(void)
{
    fa_loudness l = { .struct_size = sizeof(l) };
    fa_fingerprint id = { .struct_size = sizeof(id), .key = 5, .audio_bytes = 6 };
    fa_scan_options so;
    fa_codec_params cp;
    fa_scan_feed* f = NULL;
    static float x[2 * 96000];
    int i;
    for (i = 0; i < 2 * 96000; i++) x[i] = (float)(0.1 * sin(2.0 * PI * 440.0 * (i / 2) / 48000.0));
    fa_scan_options_init(&so);
    so.identity = &id;
    fed_params(&cp, 48000);
    CHECK_EQ(fa_scan_open_packets(g_engine, &cp, &so, &f), FA_OK);
    CHECK_EQ(feed_packets(f, x, 96000, 0), FA_OK);
    CHECK_EQ(fa_scan_wants_more(f), 1);
    CHECK_EQ(fa_scan_finish(f, &l), FA_OK);
    CHECK_EQ(fa_scan_wants_more(f), 0);
    CHECK(l.integrated_lufs < -10.0 && l.integrated_lufs > -40.0);
    fa_scan_close(f);
}

/* ---- limiter ------------------------------------------------------------------------- */

static void test_limiter(void)
{
    fa_limiter lim;
    fa_truepeak tp;
    uint32_t rate = 48000, i, n = rate;
    float* x = (float*)malloc((size_t)n * 2 * sizeof(float));
    float* orig = (float*)malloc((size_t)n * 2 * sizeof(float));
    float ceiling = (float)pow(10.0, -1.0 / 20.0);
    double out_tp;
    CHECK_EQ(fa_limiter_init(&lim, rate, 2), FA_OK);

    /* Below the ceiling: passes unchanged, only delayed. */
    for (i = 0; i < n; i++) orig[i * 2] = orig[i * 2 + 1] = x[i * 2] = x[i * 2 + 1] = (float)(0.5 * sin(2.0 * PI * 997.0 * i / rate));
    fa_limiter_process(&lim, x, n, ceiling, 1);
    for (i = (uint32_t)lim.delay; i < n; i++) CHECK(x[i * 2] == orig[(i - (uint32_t)lim.delay) * 2]);

    /* 6 dB over: true peak of the output stays at the ceiling. A burst on
     * silence checks that the look-ahead catches the very first peak. */
    for (i = 0; i < n; i++) {
        float v = i < rate / 4 ? 0.0f : (float)(2.0 * sin(2.0 * PI * 3001.0 * i / rate + 0.3));
        x[i * 2] = x[i * 2 + 1] = v;
    }
    fa_limiter_process(&lim, x, n, ceiling, 1);
    fa_truepeak_init(&tp, rate, 2);
    for (i = 0; i < n; i++) { float m = fa_truepeak_push(&tp, x + i * 2); if (m > tp.peak) tp.peak = m; }
    out_tp = 20.0 * log10(tp.peak);
    fa_truepeak_free(&tp);
    CHECK(out_tp <= -1.0 + 0.1);
    CHECK(out_tp > -1.6);                          /* limits, does not crush */

    /* Turned off: the reduction releases smoothly (no click) and then the audio
     * passes unchanged, delayed. */
    for (i = 0; i < n; i++) x[i * 2] = x[i * 2 + 1] = orig[i * 2] = (float)(1.5 * sin(2.0 * PI * 440.0 * i / rate));
    fa_limiter_process(&lim, x, n, ceiling, 0);
    for (i = (uint32_t)lim.delay + 1; i < rate / 100; i++) CHECK(fabs(x[i * 2] - x[(i - 1) * 2]) < 0.1);
    fa_limiter_free(&lim);
    CHECK_EQ(fa_limiter_init(&lim, rate, 2), FA_OK);
    for (i = 0; i < n; i++) x[i * 2] = x[i * 2 + 1] = orig[i * 2];
    fa_limiter_process(&lim, x, n, ceiling, 0);
    for (i = (uint32_t)lim.delay; i < n; i++) CHECK(x[i * 2] == orig[(i - (uint32_t)lim.delay) * 2]);
    fa_limiter_free(&lim);
    free(x);
    free(orig);

    CHECK_EQ(fa_engine_set_limiter(g_engine, FA_LIMITER_ON, -1.0), FA_OK);
    CHECK_EQ(fa_engine_set_limiter(g_engine, FA_LIMITER_AUTO, 1.0), FA_ERROR_INVALID_ARG);
    CHECK_EQ(fa_engine_set_limiter(g_engine, FA_LIMITER_AUTO, -1.0), FA_OK);
}

/* After a reduction the gain releases all the way back to 1: quiet audio a few
 * seconds later passes unchanged, not about 0.002 dB down (the release step
 * stops moving short of 1 in float). */
static void test_limiter_releases_to_unity(void)
{
    fa_limiter lim;
    uint32_t rate = 48000, i, n = rate * 4;
    float* x = (float*)malloc((size_t)n * 2 * sizeof(float));
    float* orig = (float*)malloc((size_t)n * 2 * sizeof(float));
    float ceiling = (float)pow(10.0, -1.0 / 20.0);
    CHECK_EQ(fa_limiter_init(&lim, rate, 2), FA_OK);
    for (i = 0; i < n; i++) {
        float v = (float)((i < rate / 2 ? 2.0 : 0.5) * sin(2.0 * PI * 997.0 * i / rate));
        x[i * 2] = x[i * 2 + 1] = orig[i * 2] = orig[i * 2 + 1] = v;
    }
    fa_limiter_process(&lim, x, n, ceiling, 1);
    CHECK(lim.gain == 1.0f);
    for (i = n - rate / 2; i < n; i++) CHECK(x[i * 2] == orig[(i - (uint32_t)lim.delay) * 2]);
    fa_limiter_free(&lim);
    free(x);
    free(orig);
}

/* The limiter measures true peaks a block at a time: each frame's value must be
 * exactly what pushing frame by frame gives, at every oversampling (4, 2 and 1
 * phases) and channel count, on odd block sizes, and a block skipped as unable to
 * reach the floor must really stay at or below it. */
static void test_truepeak_block_matches_push(void)
{
    static const uint32_t rates[3] = { 48000, 96000, 192000 }, chans[3] = { 1, 2, 6 };
    static const uint32_t sizes[6] = { 1, 3, 7, 113, 256, 64 };
    float floor_level = (float)pow(10.0, -1.0 / 20.0);
    uint32_t r, c, n = 4000;
    float* x = (float*)malloc((size_t)n * 6 * sizeof(float));
    float peaks[FA_TP_PUSH_BLOCK];
    srand(7);
    for (r = 0; r < 3; r++) {
        for (c = 0; c < 3; c++) {
            fa_truepeak a, b;
            uint32_t ch = chans[c], i, at = 0, k = 0, measured = 0, skipped = 0;
            /* Loud noise, then quiet passages the block test can skip, then loud again. */
            for (i = 0; i < n * ch; i++) {
                uint32_t f = i / ch;
                float amp = f < 1000 ? 1.4f : f < 3000 ? 0.05f : 0.9f;
                x[i] = amp * ((float)rand() / (float)RAND_MAX * 2.0f - 1.0f);
            }
            CHECK_EQ(fa_truepeak_init(&a, rates[r], ch), FA_OK);
            CHECK_EQ(fa_truepeak_init(&b, rates[r], ch), FA_OK);
            while (at < n) {
                uint32_t m = sizes[k++ % 6];
                int got;
                if (m > n - at) m = n - at;
                got = fa_truepeak_push_block(&b, x + (size_t)at * ch, m, peaks, floor_level);
                for (i = 0; i < m; i++) {
                    float want = fa_truepeak_push(&a, x + (size_t)(at + i) * ch);
                    if (got) CHECK(peaks[i] == want);
                    else CHECK(want <= floor_level);
                }
                measured += got;
                skipped += !got;
                at += m;
            }
            CHECK(measured > 0);
            CHECK(skipped > 0);
            fa_truepeak_free(&a);
            fa_truepeak_free(&b);
        }
    }
    free(x);
}

/* The AVX true-peak path (chosen at run time on x86) sums each lane exactly as
 * the SSE / plain path does, so both give the same peak to the bit. */
static void test_truepeak_avx_matches_sse(void)
{
    uint32_t ch = 3, n = 20000, i;
    float* x = (float*)malloc((size_t)n * ch * sizeof(float));
    double tp[2];
    int pass;
    srand(11);
    for (i = 0; i < n * ch; i++) x[i] = 0.9f * ((float)rand() / (float)RAND_MAX * 2.0f - 1.0f);
    for (pass = 0; pass < 2; pass++) {
        fa_meter m;
        CHECK_EQ(fa_meter_init(&m, 44100, ch, 1, 0), FA_OK);
        if (pass == 1) m.tp->avx = 0;
        CHECK_EQ(fa_meter_process(&m, x, n), FA_OK);
        fa_truepeak_flush(m.tp);
        tp[pass] = fa_meter_peak_dbtp(&m);
        fa_meter_free(&m);
    }
    CHECK(tp[0] == tp[1]);
    free(x);
}

/* Largest step between neighbouring output samples (channel 0). */
static double max_step(const float* x, uint32_t from, uint32_t to)
{
    double m = 0.0;
    uint32_t i;
    for (i = from + 1; i < to; i++) if (fabs(x[i * 2] - x[(i - 1) * 2]) > m) m = fabs(x[i * 2] - x[(i - 1) * 2]);
    return m;
}

static void test_limiter_switching_is_smooth(void)
{
    /* A 220 Hz sine at 1.5 (its own steepest step is 0.043): turning the limiter
     * on, or lowering its ceiling, mid-signal must not clip or click. */
    fa_limiter lim;
    uint32_t rate = 48000, i, n = rate / 2, half = n / 2;
    float* x = (float*)malloc((size_t)n * 2 * sizeof(float));
    float c1 = (float)pow(10.0, -1.0 / 20.0), c6 = (float)pow(10.0, -6.0 / 20.0);
    CHECK_EQ(fa_limiter_init(&lim, rate, 2), FA_OK);
    for (i = 0; i < n; i++) x[i * 2] = x[i * 2 + 1] = (float)(1.5 * sin(2.0 * PI * 220.0 * i / rate));
    fa_limiter_process(&lim, x, half, c1, 0);
    fa_limiter_process(&lim, x + (size_t)half * 2, n - half, c1, 1);
    CHECK(max_step(x, (uint32_t)lim.delay + 1, n) < 0.06);
    CHECK(peak_of(x + (size_t)(half + rate / 20) * 2, (size_t)(n - half - rate / 20) * 2) <= c1 + 1e-6);

    for (i = 0; i < n; i++) x[i * 2] = x[i * 2 + 1] = (float)(1.5 * sin(2.0 * PI * 220.0 * (i + n) / rate));
    fa_limiter_process(&lim, x, half, c1, 1);
    fa_limiter_process(&lim, x + (size_t)half * 2, n - half, c6, 1);
    CHECK(max_step(x, 0, n) < 0.06);
    CHECK(peak_of(x, (size_t)(half + lim.delay) * 2) <= c1 + 1e-6);
    CHECK(peak_of(x + (size_t)(half + lim.delay) * 2, (size_t)(n - half - lim.delay) * 2) <= c6 + 1e-6);
    fa_limiter_free(&lim);
    free(x);
}

static void test_scan_release_races(void)
{
    /* Close streams at many points of their scan: the job must never be freed
     * while a scan thread is still merging it (run under the sanitizers). */
    int i;
    CHECK_EQ(fa_engine_set_normalization(g_engine, FA_NORMALIZE_TRACK, -16.0), FA_OK);
    for (i = 0; i < 40; i++) {
        fa_stream* s = open_sine(45.0, -20.0, 0, NULL, 0);
        CHECK(s != NULL);
        if (!s) return;
        fa_sleep_ms((uint32_t)(i % 8) * 3);
        fa_stream_close(s);
    }
}

static void test_latency_includes_limiter(void)
{
    fa_time t;
    CHECK_EQ(fa_device_get_latency(g_dev, &t), FA_OK);
    CHECK(t >= (fa_time)((double)g_dev->limiter.delay * 1e9 / g_dev->rate));
}

int main(void)
{
    fa_engine_config cfg;
#if defined(__APPLE__)
    /* The iOS simulator starts programs elsewhere: tools/ios-sim/run.sh names the folder. */
    if (getenv("FA_TEST_CWD") && chdir(getenv("FA_TEST_CWD")) != 0) return 1;
#endif
    fa_engine_config_init(&cfg);
    cfg.flags = FA_ENGINE_NULL_OUTPUT | FA_ENGINE_NO_CACHE;
    cfg.worker_threads = 2;
    if (fa_engine_create(&cfg, &g_engine) != FA_OK) { fprintf(stderr, "engine create failed\n"); return 1; }
    if (fa_device_open(g_engine, NULL, &g_dev) != FA_OK) { fprintf(stderr, "device open failed\n"); return 1; }
    printf("null device at %u Hz (not started: this test is the audio thread)\n", g_dev->rate);

    RUN(test_ebu_3341_integrated);
    RUN(test_ebu_3342_range);
    RUN(test_true_peak);
    RUN(test_true_peak_high_frequencies);
    RUN(test_true_peak_includes_samples);
    RUN(test_true_peak_at_end_of_file);
    RUN(test_surround_weights);
    RUN(test_quad_and_61_weights);
    RUN(test_chunked_scan_matches_one_pass);
    RUN(test_cache);
    RUN(test_scan_errors);
    RUN(test_tag_parsers);
    RUN(test_stream_gain_from_scan);
    RUN(test_glide_to_new_loudness);
    RUN(test_tags_win_and_album_mode);
    RUN(test_opt_out_and_off);
    RUN(test_running_estimate_for_io);
    RUN(test_close_during_scan);
    RUN(test_lcache_keeps_recently_used);
    RUN(test_lcache_two_writers_and_a_torn_record);
    RUN(test_lcache_one_per_folder);
    RUN(test_lcache_two_processes);
    RUN(test_lcache_damaged_file);
    RUN(test_lcache_load_time);
    RUN(test_cache_api);
    RUN(test_key_ignores_tags);
    RUN(test_key_hashes_all_of_a_wav);
    RUN(test_key_of_a_big_wav);
    RUN(test_spread_order);
    RUN(test_partial_from_finished_chunks);
    RUN(test_start_wait_holds_until_final);
    RUN(test_start_wait_runs_out);
    RUN(test_start_wait_nearly_done_waits_longer);
    RUN(test_scheduled_start_never_waits);
    RUN(test_fed_scan_matches_path_scan);
    RUN(test_fed_scan_errors);
    RUN(test_fed_scan_reaches_streams_and_cache);
    RUN(test_cache_folder_and_info);
    RUN(test_fed_scan_without_cache);
    RUN(test_limiter);
    RUN(test_limiter_releases_to_unity);
    RUN(test_truepeak_block_matches_push);
    RUN(test_truepeak_avx_matches_sse);
    RUN(test_limiter_switching_is_smooth);
    RUN(test_scan_release_races);
    RUN(test_latency_includes_limiter);

    fa_device_close(g_dev);
    fa_engine_destroy(g_engine);
    if (g_failures) { printf("%d failure(s)\n", g_failures); return 1; }
    printf("all passed\n");
    return 0;
}
