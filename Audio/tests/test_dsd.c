/* DSD to PCM (dsd.c) and the DSF / DSDIFF readers (dec_dsd.c). The filters are checked
 * against their stated pass and stop bands; the files are made here, from sines run
 * through a second-order delta-sigma modulator, so the level, noise and channels that
 * come out are known. */
#if !defined(_WIN32)
#  define _POSIX_C_SOURCE 200809L
#endif
#include "fa_internal.h"
#include "test_util.h"

static fa_engine* g_engine;

/* ---- the filters ----------------------------------------------------------------- */

/* |H(f)| in dB of `n` taps at frequency f (cycles per sample). */
static double response_db(const float* h, uint32_t n, double f)
{
    double re = 0.0, im = 0.0;
    uint32_t i;
    for (i = 0; i < n; i++) {
        re += h[i] * cos(2.0 * FA_PI * f * i);
        im -= h[i] * sin(2.0 * FA_PI * f * i);
    }
    return 10.0 * log10(re * re + im * im + 1e-300);
}

/* Worst (largest) and best (smallest) response over [f0, f1] Hz at `rate`, on `steps` points. */
static void band(const float* h, uint32_t n, double rate, double f0, double f1, int steps, double* lo, double* hi)
{
    int i;
    *lo = 1e9;
    *hi = -1e9;
    for (i = 0; i <= steps; i++) {
        double db = response_db(h, n, (f0 + (f1 - f0) * i / steps) / rate);
        if (db < *lo) *lo = db;
        if (db > *hi) *hi = db;
    }
}

static void check_filters(uint32_t dsd_rate, uint32_t want)
{
    fa_dsd_conv* c = NULL;
    const float *h1, *h2;
    uint32_t n1, n2, r1, out;
    double fp, stop2, stop1, lo, hi, gain;
    CHECK_EQ(fa_dsd_conv_create(dsd_rate, 2, want, &c), FA_OK);
    out = fa_dsd_conv_rate(c);
    CHECK_EQ(out, want);
    fa_dsd_conv_filters(c, &h1, &n1, &h2, &n2, &r1);
    CHECK_EQ(n1 % 8, 0);
    CHECK_EQ(fa_dsd_conv_frame_bytes(c) * 8, dsd_rate / out);
    fp = out <= 48000 ? 0.4535 * out : 0.34 * out;
    stop2 = n2 ? (out <= 48000 ? out - fp : out * 0.5) : 0.0;
    stop1 = n2 ? r1 - stop2 : r1 * 0.5;
    /* Stage 1 carries the level: its DC gain is FA_DSD_GAIN. */
    gain = 20.0 * log10(FA_DSD_GAIN);
    band(h1, n1, dsd_rate, 0.0, fp, 200, &lo, &hi);
    CHECK_NEAR(lo, gain, 0.0005);
    CHECK_NEAR(hi, gain, 0.0005);
    band(h1, n1, dsd_rate, stop1, dsd_rate * 0.5, 4000, &lo, &hi);
    CHECK(hi < gain - 115.0);
    if (n2) {
        band(h2, n2, r1, 0.0, fp, 200, &lo, &hi);
        CHECK_NEAR(lo, 0.0, 0.0005);
        CHECK_NEAR(hi, 0.0, 0.0005);
        band(h2, n2, r1, stop2, r1 * 0.5, 2000, &lo, &hi);
        CHECK(hi < -115.0);
    }
    fa_dsd_conv_free(c);
}

static void test_filters(void)
{
    static const uint32_t mult[] = { 64, 128, 256, 512, 1024 };
    uint32_t m, j;
    for (m = 0; m < 5; m++)
        for (j = 0; j < 4; j++) {
            check_filters(44100 * mult[m], 44100u << j);
            if (g_failures) { fprintf(stderr, "  DSD%u -> %u\n", mult[m], 44100u << j); return; }
        }
    for (j = 0; j < 4; j++) check_filters(48000 * 64, 48000u << j);
}

static void test_rates(void)
{
    uint32_t base = 0;
    fa_dsd_conv* c = NULL;
    CHECK_EQ(fa_dsd_rate_family(2822400, &base), 64);
    CHECK_EQ(base, 44100);
    CHECK_EQ(fa_dsd_rate_family(3072000, &base), 64);
    CHECK_EQ(base, 48000);
    CHECK_EQ(fa_dsd_rate_family(45158400, &base), 1024);
    CHECK_EQ(fa_dsd_rate_family(1411200, NULL), 0);    /* DSD32: not byte-aligned per output, refused */
    CHECK_EQ(fa_dsd_rate_family(2822401, NULL), 0);
    CHECK_EQ(fa_dsd_out_rate(2822400, 0), 88200);
    CHECK_EQ(fa_dsd_out_rate(3072000, 0), 96000);
    CHECK_EQ(fa_dsd_out_rate(2822400, 48000), 44100);
    CHECK_EQ(fa_dsd_out_rate(2822400, 96000), 88200);
    CHECK_EQ(fa_dsd_out_rate(2822400, 192000), 176400);
    CHECK_EQ(fa_dsd_out_rate(2822400, 1), 44100);
    CHECK_EQ(fa_dsd_out_rate(2822400, 0xFFFFFFFFu), 352800);
    CHECK_EQ(fa_dsd_conv_create(1411200, 2, 0, &c), FA_ERROR_UNSUPPORTED_FORMAT);
    CHECK_EQ(fa_dsd_conv_create(2822400, 0, 0, &c), FA_ERROR_UNSUPPORTED_FORMAT);
    CHECK_EQ(fa_dsd_conv_create(2822400, 33, 0, &c), FA_ERROR_UNSUPPORTED_FORMAT);
}

/* ---- making DSD ---------------------------------------------------------------- */

typedef struct tone { double freq, amp; } tone;

/* Second-order delta-sigma modulator, noise transfer (1 - 1/z)^2, signal transfer 1: per
 * channel `bytes` bytes MSB first (oldest bit highest), channel c at out + c * bytes. */
static uint8_t* modulate(uint32_t rate, uint32_t channels, size_t bytes, const tone* tones, int per_channel)
{
    uint8_t* out = (uint8_t*)calloc(channels, bytes);
    uint32_t c, seed = 12345;
    if (!out) return NULL;
    for (c = 0; c < channels; c++) {
        double q1 = 0.0, q2 = 0.0;
        size_t i;
        for (i = 0; i < bytes * 8; i++) {
            double t = (double)i / rate, x = 0.0, v, y;
            int k;
            for (k = 0; k < per_channel; k++) x += tones[c * per_channel + k].amp * sin(2.0 * FA_PI * tones[c * per_channel + k].freq * t);
            seed = seed * 1664525u + 1013904223u;
            x += ((double)(seed >> 8) / 16777216.0 - 0.5) * 1e-3;   /* a little dither against idle tones */
            v = x - 2.0 * q1 + q2;
            y = v >= 0.0 ? 1.0 : -1.0;
            q2 = q1;
            q1 = y - v;
            if (y > 0) out[c * bytes + i / 8] |= (uint8_t)(0x80 >> (i % 8));
        }
    }
    return out;
}

typedef struct buf { uint8_t* d; size_t n, cap; } buf;

static void put(buf* b, const void* p, size_t n)
{
    if (b->n + n > b->cap) {
        b->cap = (b->n + n) * 2 + 64;
        b->d = (uint8_t*)realloc(b->d, b->cap);
    }
    memcpy(b->d + b->n, p, n);
    b->n += n;
}
static void le32(buf* b, uint32_t v) { uint8_t x[4] = { (uint8_t)v, (uint8_t)(v >> 8), (uint8_t)(v >> 16), (uint8_t)(v >> 24) }; put(b, x, 4); }
static void le64(buf* b, uint64_t v) { le32(b, (uint32_t)v); le32(b, (uint32_t)(v >> 32)); }
static void be16(buf* b, uint32_t v) { uint8_t x[2] = { (uint8_t)(v >> 8), (uint8_t)v }; put(b, x, 2); }
static void be32(buf* b, uint32_t v) { uint8_t x[4] = { (uint8_t)(v >> 24), (uint8_t)(v >> 16), (uint8_t)(v >> 8), (uint8_t)v }; put(b, x, 4); }
static void be64(buf* b, uint64_t v) { be32(b, (uint32_t)(v >> 32)); be32(b, (uint32_t)v); }

static uint8_t rev8(uint8_t v)
{
    uint8_t r = 0;
    int i;
    for (i = 0; i < 8; i++) if (v & (1 << i)) r |= (uint8_t)(0x80 >> i);
    return r;
}

/* A DSF file of `bits` (per-channel planes, MSB first): msb_first = 0 stores them LSB first,
 * as almost every file does. `id3` (may be NULL) is put at the end. */
static buf make_dsf(const uint8_t* bits, uint32_t rate, uint32_t channels, uint32_t type, size_t bytes, int msb_first,
                    const uint8_t* id3, size_t id3_len)
{
    buf b = { 0 };
    size_t groups = (bytes + 4095) / 4096, g, c, i;
    uint64_t data = 12 + (uint64_t)groups * 4096 * channels, total = 28 + 52 + data + id3_len;
    put(&b, "DSD ", 4); le64(&b, 28); le64(&b, total); le64(&b, id3 ? 28 + 52 + data : 0);
    put(&b, "fmt ", 4); le64(&b, 52); le32(&b, 1); le32(&b, 0); le32(&b, type); le32(&b, channels);
    le32(&b, rate); le32(&b, msb_first ? 8 : 1); le64(&b, (uint64_t)bytes * 8); le32(&b, 4096); le32(&b, 0);
    put(&b, "data", 4); le64(&b, data);
    for (g = 0; g < groups; g++)
        for (c = 0; c < channels; c++)
            for (i = 0; i < 4096; i++) {
                size_t at = g * 4096 + i;
                uint8_t v = at < bytes ? bits[c * bytes + at] : 0;
                if (!msb_first) v = rev8(v);
                put(&b, &v, 1);
            }
    if (id3) put(&b, id3, id3_len);
    return b;
}

static void dff_chunk(buf* b, const char* id, const buf* body)
{
    put(b, id, 4);
    be64(b, body->n);
    put(b, body->d, body->n);
    if (body->n & 1) put(b, "", 1);
}

/* A DSDIFF file: file channel k is the plane `from[k]` of `bits`, named ids[k]. */
static buf make_dff(const uint8_t* bits, uint32_t rate, uint32_t channels, size_t bytes, const char* const* ids,
                    const uint32_t* from, const char* cmpr, const char* title, const char* artist)
{
    buf b = { 0 }, form = { 0 }, prop = { 0 }, c = { 0 };
    size_t i, k;
    put(&form, "DSD ", 4);
    c.n = 0; be32(&c, 0x01050000); dff_chunk(&form, "FVER", &c);
    put(&prop, "SND ", 4);
    c.n = 0; be32(&c, rate); dff_chunk(&prop, "FS  ", &c);
    c.n = 0; be16(&c, channels); for (k = 0; k < channels; k++) put(&c, ids[k], 4); dff_chunk(&prop, "CHNL", &c);
    c.n = 0; put(&c, cmpr, 4); put(&c, "\x0enot compressed", 15); dff_chunk(&prop, "CMPR", &c);
    dff_chunk(&form, "PROP", &prop);
    if (strcmp(cmpr, "DST ") == 0) {
        buf frte = { 0 };
        c.n = 0; be32(&c, 75); be16(&c, 75); dff_chunk(&frte, "FRTE", &c);   /* one second */
        dff_chunk(&form, "DST ", &frte);
        free(frte.d);
    } else {
        c.n = 0;
        for (i = 0; i < bytes; i++) for (k = 0; k < channels; k++) put(&c, &bits[from[k] * bytes + i], 1);
        dff_chunk(&form, "DSD ", &c);
    }
    if (title || artist) {
        buf diin = { 0 };
        if (title) { c.n = 0; be32(&c, (uint32_t)strlen(title)); put(&c, title, strlen(title)); dff_chunk(&diin, "DITI", &c); }
        if (artist) { c.n = 0; be32(&c, (uint32_t)strlen(artist)); put(&c, artist, strlen(artist)); dff_chunk(&diin, "DIAR", &c); }
        dff_chunk(&form, "DIIN", &diin);
        free(diin.d);
    }
    put(&b, "FRM8", 4);
    be64(&b, form.n);
    put(&b, form.d, form.n);
    free(form.d);
    free(prop.d);
    free(c.d);
    return b;
}

/* ---- reading --------------------------------------------------------------------- */

static fa_result open_mem(const buf* b, uint32_t dsd_rate, fa_stream** s)
{
    fa_open_options o;
    fa_open_options_init(&o);
    o.flags = FA_OPEN_DECODE_ONLY | FA_OPEN_NO_NORMALIZE;
    o.dsd_rate = dsd_rate;
    *s = NULL;
    return fa_stream_open_memory(g_engine, b->d, b->n, &o, s);
}

static float* read_all(fa_stream* s, uint32_t channels, int64_t* out_n)
{
    int64_t cap = 1 << 15, n = 0;
    float* all = (float*)malloc((size_t)cap * channels * sizeof(float));
    for (;;) {
        fa_frames got = 0;
        fa_result r;
        if (n + 1000 > cap) {
            cap *= 2;
            all = (float*)realloc(all, (size_t)cap * channels * sizeof(float));
        }
        r = fa_stream_read(s, all + (size_t)n * channels, 1000, &got);
        n += got;
        if (r != FA_OK || got == 0) break;
    }
    *out_n = n;
    return all;
}

/* Least-squares amplitude of a sine of `freq` in channel `ch` over [from, to). */
static double amplitude(const float* p, uint32_t channels, uint32_t ch, double rate, double freq, int64_t from, int64_t to)
{
    double ss = 0, sc = 0, cc = 0, ys = 0, yc = 0, det, a, b;
    int64_t i;
    for (i = from; i < to; i++) {
        double w = 2.0 * FA_PI * freq * i / rate, s = sin(w), c = cos(w), y = p[i * channels + ch];
        ss += s * s; sc += s * c; cc += c * c; ys += y * s; yc += y * c;
    }
    det = ss * cc - sc * sc;
    a = (ys * cc - yc * sc) / det;
    b = (yc * ss - ys * sc) / det;
    return sqrt(a * a + b * b);
}

static double hann(int64_t i, int64_t n) { return 0.5 - 0.5 * cos(2.0 * FA_PI * (double)i / (double)n); }

/* Level (dB, 0 = full scale) of a sine of `freq` in channel `ch` over [from, from + n),
 * from a Hann-windowed DFT at exactly that frequency. */
static double tone_db(const float* p, uint32_t channels, uint32_t ch, double rate, int64_t from, int64_t n, double freq)
{
    double re = 0.0, im = 0.0, wsum = 0.0;
    int64_t i;
    for (i = 0; i < n; i++) {
        double w = hann(i, n), a = 2.0 * FA_PI * freq * (double)i / rate, x = p[(from + i) * channels + ch] * w;
        wsum += w;
        re += x * cos(a);
        im -= x * sin(a);
    }
    return 20.0 * log10(2.0 * sqrt(re * re + im * im) / wsum + 1e-300);
}

/* Power (dB, relative to a full-scale sine) of everything between f0 and f1 Hz in channel
 * `ch` over [from, from + n) once the sine of `freq` is taken out (least squares). */
static double noise_db(const float* p, uint32_t channels, uint32_t ch, double rate, int64_t from, int64_t n, double f0, double f1, double freq)
{
    double ss = 0, sc = 0, cc = 0, ys = 0, yc = 0, det, a, b, total = 0.0, w2 = 0.0;
    double* r = (double*)malloc(sizeof(double) * (size_t)n);
    int64_t i, k, k0 = (int64_t)ceil(f0 * n / rate), k1 = (int64_t)floor(f1 * n / rate);
    for (i = 0; i < n; i++) {
        double w = 2.0 * FA_PI * freq * (double)i / rate, s = sin(w), c = cos(w), y = p[(from + i) * channels + ch];
        ss += s * s; sc += s * c; cc += c * c; ys += y * s; yc += y * c;
    }
    det = ss * cc - sc * sc;
    a = (ys * cc - yc * sc) / det;
    b = (yc * ss - ys * sc) / det;
    for (i = 0; i < n; i++) {
        double w = 2.0 * FA_PI * freq * (double)i / rate, h = hann(i, n);
        r[i] = (p[(from + i) * channels + ch] - a * sin(w) - b * cos(w)) * h;
        w2 += h * h;
    }
    for (k = k0; k <= k1; k++) {
        double re = 0.0, im = 0.0, pr = 1.0, pi = 0.0, cr = cos(2.0 * FA_PI * k / n), ci = -sin(2.0 * FA_PI * k / n);
        for (i = 0; i < n; i++) {
            double t;
            re += r[i] * pr;
            im += r[i] * pi;
            t = pr * cr - pi * ci;
            pi = pr * ci + pi * cr;
            pr = t;
        }
        total += 2.0 * (re * re + im * im) / ((double)n * w2);
    }
    free(r);
    return 10.0 * log10(total / 0.5 + 1e-300);
}

#define DSD64 2822400u

/* Stereo: 1 kHz at 0.4 on the left, 1.5 kHz at 0.25 on the right (DSD units, 1 = 100 % modulation). */
static const tone g_lr[2] = { { 1000.0, 0.4 }, { 1500.0, 0.25 } };

static void check_tones(const float* pcm, int64_t n, uint32_t channels, uint32_t rate, const tone* t)
{
    uint32_t c;
    int64_t from = 2000, to = n - 200;
    for (c = 0; c < channels; c++) {
        double a = amplitude(pcm, channels, c, rate, t[c].freq, from, to);
        CHECK_NEAR(20.0 * log10(a / (t[c].amp * FA_DSD_GAIN)), 0.0, 0.01);
    }
}

static void test_dsf_and_dff(void)
{
    const size_t bytes = DSD64 / 8 / 4;   /* 0.25 s */
    static const char* const ids[2] = { "SLFT", "SRGT" };
    static const uint32_t from[2] = { 0, 1 };
    uint8_t* bits = modulate(DSD64, 2, bytes, g_lr, 1);
    buf dsf = make_dsf(bits, DSD64, 2, 2, bytes, 0, NULL, 0), dsf8 = make_dsf(bits, DSD64, 2, 2, bytes, 1, NULL, 0);
    buf dff = make_dff(bits, DSD64, 2, bytes, ids, from, "DSD ", NULL, NULL);
    fa_stream *s = NULL, *s8 = NULL, *sd = NULL;
    fa_stream_info info = { .struct_size = sizeof(info) };
    float *a = NULL, *b = NULL, *d = NULL;
    int64_t na, nb, nd, i;
    CHECK_EQ(open_mem(&dsf, 0, &s), FA_OK);
    CHECK_EQ(fa_stream_get_info(s, &info), FA_OK);
    CHECK(strcmp(info.codec, "dsd") == 0);
    CHECK(strcmp(info.container, "dsf") == 0);
    CHECK_EQ(info.format.sample_rate, 88200);
    CHECK_EQ(info.format.channels, 2);
    CHECK_EQ(info.length_frames, (int64_t)bytes / 4);
    CHECK_EQ(info.bits_per_sample, 1);
    CHECK_EQ(info.bitrate, DSD64 * 2);
    CHECK(info.seekable);
    a = read_all(s, 2, &na);
    CHECK_EQ(na, (int64_t)bytes / 4);
    check_tones(a, na, 2, 88200, g_lr);
    /* The noise in the audio band is the modulator's own (about -80 dB for a second-order
     * one at 64x, 70 dB under the tone): nothing of the large ultrasonic noise folded down,
     * which would leave next to no gap. */
    {
        double sig = tone_db(a, 2, 0, 88200, 4000, 16384, 1000.0), noise = noise_db(a, 2, 0, 88200, 4000, 16384, 20.0, 20000.0, 1000.0);
        printf("  1 kHz %.3f dB, noise to 20 kHz %.1f dB\n", sig, noise);
        CHECK(noise - sig < -62.0);
    }

    /* The same bits MSB first (DSF bits_per_sample 8) and in DSDIFF: the same samples. */
    CHECK_EQ(open_mem(&dsf8, 0, &s8), FA_OK);
    b = read_all(s8, 2, &nb);
    CHECK_EQ(nb, na);
    CHECK_EQ(open_mem(&dff, 0, &sd), FA_OK);
    CHECK_EQ(fa_stream_get_info(sd, &info), FA_OK);
    CHECK(strcmp(info.container, "dff") == 0);
    CHECK_EQ(info.length_frames, na);
    d = read_all(sd, 2, &nd);
    CHECK_EQ(nd, na);
    for (i = 0; i < na * 2; i++) {
        if (a[i] != b[i] || a[i] != d[i]) break;
    }
    CHECK_EQ(i, na * 2);
    free(a); free(b); free(d);
    fa_stream_close(s); fa_stream_close(s8); fa_stream_close(sd);
    free(dsf.d); free(dsf8.d); free(dff.d); free(bits);
}

/* Tones the filters must remove: 60 kHz (would fold to 28.2 kHz at 88.2 kHz) and 340 kHz
 * (would fold to 12.8 kHz at stage 1's 352.8 kHz). */
static void test_ultrasonic_rejected(void)
{
    const size_t bytes = DSD64 / 8 / 4;
    static const tone t[3] = { { 1000.0, 0.2 }, { 60000.0, 0.2 }, { 340000.0, 0.2 } };
    uint8_t* bits = modulate(DSD64, 1, bytes, t, 3);
    buf dsf = make_dsf(bits, DSD64, 1, 1, bytes, 0, NULL, 0);
    fa_stream* s = NULL;
    float* p;
    int64_t n;
    double ref;
    CHECK_EQ(open_mem(&dsf, 0, &s), FA_OK);
    p = read_all(s, 1, &n);
    ref = tone_db(p, 1, 0, 88200, 4000, 16384, 1000.0);
    CHECK_NEAR(ref, 20.0 * log10(0.2 * FA_DSD_GAIN), 0.01);
    printf("  fold of 60 kHz %.1f dB, of 340 kHz %.1f dB\n", tone_db(p, 1, 0, 88200, 4000, 16384, 28200.0) - ref,
           tone_db(p, 1, 0, 88200, 4000, 16384, 12800.0) - ref);
    CHECK(tone_db(p, 1, 0, 88200, 4000, 16384, 28200.0) - ref < -95.0);
    CHECK(tone_db(p, 1, 0, 88200, 4000, 16384, 12800.0) - ref < -95.0);
    free(p);
    fa_stream_close(s);
    free(dsf.d);
    free(bits);
}

/* Every output rate, DSD128 and DSD on the 48 kHz family: the level is right everywhere. */
static void test_rates_and_multiples(void)
{
    static const struct { uint32_t dsd, want, rate; } cases[] = {
        { DSD64, 44100, 44100 }, { DSD64, 176400, 176400 }, { DSD64, 352800, 352800 }, { DSD64, 50000, 44100 },
        { DSD64 * 2, 0, 88200 }, { 3072000, 0, 96000 }, { 3072000 * 2, 192000, 192000 },
    };
    size_t k;
    for (k = 0; k < sizeof(cases) / sizeof(cases[0]); k++) {
        const size_t bytes = cases[k].dsd / 8 / 8;   /* 0.125 s */
        uint8_t* bits = modulate(cases[k].dsd, 2, bytes, g_lr, 1);
        buf dsf = make_dsf(bits, cases[k].dsd, 2, 2, bytes, 0, NULL, 0);
        fa_stream* s = NULL;
        fa_stream_info info = { .struct_size = sizeof(info) };
        float* p;
        int64_t n;
        CHECK_EQ(open_mem(&dsf, cases[k].want, &s), FA_OK);
        CHECK_EQ(fa_stream_get_info(s, &info), FA_OK);
        CHECK_EQ(info.format.sample_rate, cases[k].rate);
        CHECK_EQ(info.length_frames, (int64_t)bytes * 8 / (cases[k].dsd / cases[k].rate));
        p = read_all(s, 2, &n);
        CHECK_EQ(n, info.length_frames);
        check_tones(p, n, 2, cases[k].rate, g_lr);
        if (g_failures) fprintf(stderr, "  case %u\n", (unsigned)k);
        free(p);
        fa_stream_close(s);
        free(dsf.d);
        free(bits);
    }
}

/* A seek anywhere gives exactly the samples of reading through. */
static void test_seeks_exact(void)
{
    const size_t bytes = DSD64 / 8 / 4;
    static const int64_t targets[] = { 0, 1, 7, 100, 1023, 1024, 5000, 12345, 22049, 3, 22050 };
    static const char* const ids[2] = { "SLFT", "SRGT" };
    static const uint32_t from[2] = { 0, 1 };
    static const uint32_t rates[3] = { 0, 44100, 352800 };
    uint8_t* bits = modulate(DSD64, 2, bytes, g_lr, 1);
    int file, r;
    for (file = 0; file < 2; file++)
        for (r = 0; r < 3; r++) {
            buf f = file ? make_dff(bits, DSD64, 2, bytes, ids, from, "DSD ", NULL, NULL) : make_dsf(bits, DSD64, 2, 2, bytes, 0, NULL, 0);
            fa_stream* s = NULL;
            float* full;
            float chunk[2 * 500];
            int64_t n;
            size_t t;
            CHECK_EQ(open_mem(&f, rates[r], &s), FA_OK);
            full = read_all(s, 2, &n);
            for (t = 0; t < sizeof(targets) / sizeof(targets[0]); t++) {
                fa_frames got = 0;
                int64_t at = targets[t] * (rates[r] ? (int64_t)rates[r] : 88200) / 88200, i;
                CHECK_EQ(fa_stream_seek(s, at), FA_OK);
                fa_stream_read(s, chunk, 500, &got);
                CHECK_EQ(got, at + 500 <= n ? 500 : n - at);
                for (i = 0; i < got * 2; i++) if (chunk[i] != full[at * 2 + i]) break;
                if (i != got * 2) fprintf(stderr, "  file %d rate %u seek %lld: sample %lld differs\n", file, rates[r], (long long)at, (long long)i);
                CHECK_EQ(i, got * 2);
            }
            free(full);
            fa_stream_close(s);
            free(f.d);
        }
    free(bits);
}

/* DSDIFF channels in another order come out in WAVE order; three or more get a speaker mask. */
static void test_dff_channel_order(void)
{
    const size_t bytes = DSD64 / 8 / 8;
    static const tone t[3] = { { 1000.0, 0.4 }, { 1500.0, 0.25 }, { 2500.0, 0.3 } };   /* L, R, C */
    static const char* const ids2[2] = { "SRGT", "SLFT" };
    static const uint32_t from2[2] = { 1, 0 };
    static const char* const ids3[3] = { "C   ", "MRGT", "MLFT" };
    static const uint32_t from3[3] = { 2, 1, 0 };
    static const char* const odd[2] = { "C001", "C002" };
    static const uint32_t same[2] = { 0, 1 };
    uint8_t* bits = modulate(DSD64, 3, bytes, t, 1);
    buf f2 = make_dff(bits, DSD64, 2, bytes, ids2, from2, "DSD ", NULL, NULL);
    buf f3 = make_dff(bits, DSD64, 3, bytes, ids3, from3, "DSD ", NULL, NULL);
    buf fo = make_dff(bits, DSD64, 2, bytes, odd, same, "DSD ", NULL, NULL);
    fa_stream* s = NULL;
    fa_stream_info info = { .struct_size = sizeof(info) };
    float* p;
    int64_t n;
    CHECK_EQ(open_mem(&f2, 0, &s), FA_OK);
    CHECK_EQ(fa_stream_get_info(s, &info), FA_OK);
    CHECK_EQ(info.format.channel_mask, 0);
    p = read_all(s, 2, &n);
    check_tones(p, n, 2, 88200, t);
    free(p);
    fa_stream_close(s);
    CHECK_EQ(open_mem(&f3, 0, &s), FA_OK);
    CHECK_EQ(fa_stream_get_info(s, &info), FA_OK);
    CHECK_EQ(info.format.channels, 3);
    CHECK_EQ(info.format.channel_mask, 0x7);
    p = read_all(s, 3, &n);
    check_tones(p, n, 3, 88200, t);
    free(p);
    fa_stream_close(s);
    /* Channels it cannot name stay in the file's order. */
    CHECK_EQ(open_mem(&fo, 0, &s), FA_OK);
    p = read_all(s, 2, &n);
    check_tones(p, n, 2, 88200, t);
    free(p);
    fa_stream_close(s);
    free(f2.d); free(f3.d); free(fo.d); free(bits);
}

static void test_dsf_channel_types(void)
{
    const size_t bytes = 4096 * 2;
    static const tone t[6] = { { 1000, 0.1 }, { 1100, 0.1 }, { 1200, 0.1 }, { 1300, 0.1 }, { 1400, 0.1 }, { 1500, 0.1 } };
    static const struct { uint32_t type, channels; uint64_t mask; } cases[] = {
        { 1, 1, 0 }, { 2, 2, 0 }, { 3, 3, 0x7 }, { 4, 4, 0x33 }, { 5, 4, 0xF }, { 6, 5, 0x37 }, { 7, 6, 0x3F }, { 9, 6, 0 },
    };
    size_t k;
    uint8_t* bits = modulate(DSD64, 6, bytes, t, 1);
    for (k = 0; k < sizeof(cases) / sizeof(cases[0]); k++) {
        buf f = make_dsf(bits, DSD64, cases[k].channels, cases[k].type, bytes, 0, NULL, 0);
        fa_stream* s = NULL;
        fa_stream_info info = { .struct_size = sizeof(info) };
        CHECK_EQ(open_mem(&f, 0, &s), FA_OK);
        CHECK_EQ(fa_stream_get_info(s, &info), FA_OK);
        CHECK_EQ(info.format.channels, cases[k].channels);
        CHECK_EQ(info.format.channel_mask, cases[k].mask);
        fa_stream_close(s);
        free(f.d);
    }
    free(bits);
}

/* The sample count ends the audio inside the last (padded) block; a cut file keeps whole blocks. */
static void test_dsf_lengths(void)
{
    const size_t bytes = 4096 * 3 + 1000;
    uint8_t* bits = modulate(DSD64, 2, bytes, g_lr, 1);
    buf f = make_dsf(bits, DSD64, 2, 2, bytes, 0, NULL, 0);
    fa_stream* s = NULL;
    fa_stream_info info = { .struct_size = sizeof(info) };
    float* p;
    int64_t n;
    CHECK_EQ(open_mem(&f, 0, &s), FA_OK);
    CHECK_EQ(fa_stream_get_info(s, &info), FA_OK);
    CHECK_EQ(info.length_frames, (int64_t)bytes / 4);
    p = read_all(s, 2, &n);
    CHECK_EQ(n, (int64_t)bytes / 4);
    free(p);
    fa_stream_close(s);
    /* Cut in the middle of the third block group: two groups are left. */
    f.n = 92 + 4096 * 2 * 2 + 3000;
    CHECK_EQ(open_mem(&f, 0, &s), FA_OK);
    CHECK_EQ(fa_stream_get_info(s, &info), FA_OK);
    CHECK_EQ(info.length_frames, 4096 * 2 / 4);
    p = read_all(s, 2, &n);
    CHECK_EQ(n, 4096 * 2 / 4);
    free(p);
    fa_stream_close(s);
    free(f.d);
    free(bits);
}

static void test_dff_cut_and_dst(void)
{
    const size_t bytes = 20000;
    static const char* const ids[2] = { "SLFT", "SRGT" };
    static const uint32_t from[2] = { 0, 1 };
    uint8_t* bits = modulate(DSD64, 2, bytes, g_lr, 1);
    buf f = make_dff(bits, DSD64, 2, bytes, ids, from, "DSD ", NULL, NULL);
    buf dst = make_dff(bits, DSD64, 2, bytes, ids, from, "DST ", NULL, NULL);
    fa_stream* s = NULL;
    fa_stream_info info = { .struct_size = sizeof(info) };
    fa_tags* tags = NULL;
    float* p;
    int64_t n;
    size_t whole = f.n;
    f.n = whole - 10001;    /* 10001 of the 40000 data bytes gone: 14999 bytes of each channel left */
    CHECK_EQ(open_mem(&f, 0, &s), FA_OK);
    CHECK_EQ(fa_stream_get_info(s, &info), FA_OK);
    CHECK_EQ(info.length_frames, (int64_t)(bytes * 2 - 10001) / 2 / 4);
    p = read_all(s, 2, &n);
    CHECK_EQ(n, info.length_frames);
    free(p);
    fa_stream_close(s);
    /* DST: recognised, not decoded; the tags reader still gets the details. */
    CHECK_EQ(open_mem(&dst, 0, &s), FA_ERROR_UNSUPPORTED_FORMAT);
    CHECK_EQ(fa_tags_read_memory(dst.d, dst.n, 0, &tags), FA_OK);
    info.struct_size = sizeof(info);
    CHECK_EQ(fa_tags_get_info(tags, &info), FA_OK);
    fa_tags_close(tags);
    CHECK(strcmp(info.codec, "dst") == 0);
    CHECK(strcmp(info.container, "dff") == 0);
    CHECK_EQ(info.can_decode, 0);
    CHECK_EQ(info.format.sample_rate, 88200);
    CHECK_EQ(info.format.channels, 2);
    CHECK_EQ(info.length_frames, 88200);
    free(f.d);
    free(dst.d);
    free(bits);
}

static void test_tags(void)
{
    const size_t bytes = 4096;
    static const char* const ids[2] = { "SLFT", "SRGT" };
    static const uint32_t from[2] = { 0, 1 };
    /* ID3v2.3 with TIT2 "Dsd Song" and TPE1 "Someone". */
    static const uint8_t id3[] = { 'I', 'D', '3', 3, 0, 0, 0, 0, 0, 37,
        'T', 'I', 'T', '2', 0, 0, 0, 9, 0, 0, 0, 'D', 's', 'd', ' ', 'S', 'o', 'n', 'g',
        'T', 'P', 'E', '1', 0, 0, 0, 8, 0, 0, 0, 'S', 'o', 'm', 'e', 'o', 'n', 'e' };
    uint8_t* bits = modulate(DSD64, 2, bytes, g_lr, 1);
    buf dsf = make_dsf(bits, DSD64, 2, 2, bytes, 0, id3, sizeof(id3));
    buf dff = make_dff(bits, DSD64, 2, bytes, ids, from, "DSD ", "Master Title", "Master Artist");
    fa_tags* tags = NULL;
    const char* v;
    CHECK_EQ(fa_tags_read_memory(dsf.d, dsf.n, 0, &tags), FA_OK);
    v = fa_tags_get(tags, FA_TAG_TITLE);
    CHECK(v && strcmp(v, "Dsd Song") == 0);
    v = fa_tags_get(tags, FA_TAG_ARTIST);
    CHECK(v && strcmp(v, "Someone") == 0);
    fa_tags_close(tags);
    tags = NULL;
    CHECK_EQ(fa_tags_read_memory(dff.d, dff.n, 0, &tags), FA_OK);
    v = fa_tags_get(tags, FA_TAG_TITLE);
    CHECK(v && strcmp(v, "Master Title") == 0);
    v = fa_tags_get(tags, FA_TAG_ARTIST);
    CHECK(v && strcmp(v, "Master Artist") == 0);
    fa_tags_close(tags);
    free(dsf.d);
    free(dff.d);
    free(bits);
}

/* Broken headers are refused, never read past. */
static void test_bad_headers(void)
{
    const size_t bytes = 4096;
    uint8_t* bits = modulate(DSD64, 2, bytes, g_lr, 1);
    buf f = make_dsf(bits, DSD64, 2, 2, bytes, 0, NULL, 0);
    fa_stream* s = NULL;
    size_t cut;
    uint8_t keep;
    for (cut = 1; cut < 92; cut += 7) {
        buf c = f;
        c.n = cut;
        CHECK(open_mem(&c, 0, &s) != FA_OK);
    }
    keep = f.d[56];                 /* the rate: not a DSD rate */
    f.d[56] = 0x01;
    CHECK_EQ(open_mem(&f, 0, &s), FA_ERROR_UNSUPPORTED_FORMAT);
    f.d[56] = keep;
    keep = f.d[60];                 /* bits per sample 2 */
    f.d[60] = 2;
    CHECK_EQ(open_mem(&f, 0, &s), FA_ERROR_UNSUPPORTED_FORMAT);
    f.d[60] = keep;
    f.d[72] = 0x12;                 /* block size 4114: not a multiple of a frame */
    CHECK_EQ(open_mem(&f, 0, &s), FA_ERROR_UNSUPPORTED_FORMAT);
    free(f.d);
    free(bits);
}

/* The fast path (byte tables, float sums) against the filters run directly, in double, on
 * the same bits: the difference is the converter's own arithmetic noise. */
static void check_reference(uint32_t dsd_rate, uint32_t want, size_t bytes)
{
    uint8_t* bits = modulate(dsd_rate, 1, bytes, g_lr, 1);
    fa_dsd_conv* c = NULL;
    const float *h1, *h2;
    const uint8_t* planes[1];
    uint32_t n1, n2, r1, d1, m2, out;
    size_t frames, k;
    double* y1;
    float* fast;
    double worst = 0.0;
    CHECK(bits != NULL);
    CHECK_EQ(fa_dsd_conv_create(dsd_rate, 1, want, &c), FA_OK);
    fa_dsd_conv_filters(c, &h1, &n1, &h2, &n2, &r1);
    out = fa_dsd_conv_rate(c);
    d1 = dsd_rate / r1;
    m2 = r1 / out;
    frames = bytes / fa_dsd_conv_frame_bytes(c);
    fast = (float*)malloc(sizeof(float) * frames);
    y1 = (double*)calloc(frames * m2, sizeof(double));
    planes[0] = bits;
    fa_dsd_conv_run(c, planes, 1, 0, frames, fast);
    for (k = 0; k < frames * m2; k++) {
        int64_t end = (int64_t)(k + 1) * d1 - 1, t;
        double acc = 0.0;
        if (end - (int64_t)n1 + 1 < 0) continue;
        for (t = 0; t < (int64_t)n1; t++) {
            int64_t bit = end - t;
            acc += (bits[bit / 8] >> (7 - bit % 8)) & 1 ? h1[t] : -h1[t];
        }
        y1[k] = acc;
    }
    for (k = fa_dsd_conv_preroll(c); k < frames; k++) {
        double ref = 0.0;
        uint32_t i;
        if (n2) for (i = 0; i < n2; i++) ref += h2[i] * y1[(k + 1) * m2 - n2 + i];
        else ref = y1[k];
        if (fabs(ref - fast[k]) > worst) worst = fabs(ref - fast[k]);
    }
    printf("  DSD%u -> %u: worst difference %.2e (%.1f dB)\n", dsd_rate / 44100, out, worst, 20.0 * log10(worst + 1e-30));
    CHECK(worst < 2e-6);
    free(y1);
    free(fast);
    free(bits);
    fa_dsd_conv_free(c);
}

static void test_matches_reference(void)
{
    check_reference(DSD64, 88200, 20000);
    check_reference(DSD64, 44100, 20000);
    check_reference(DSD64, 352800, 20000);
    check_reference(DSD64 * 4, 176400, 40000);
}

int main(void)
{
    fa_engine_config cfg;
    fa_engine_config_init(&cfg);
    cfg.flags = FA_ENGINE_NULL_OUTPUT | FA_ENGINE_NO_CACHE;
    cfg.worker_threads = 2;
    if (fa_engine_create(&cfg, &g_engine) != FA_OK) { fprintf(stderr, "engine create failed\n"); return 1; }
    RUN(test_rates);
    RUN(test_filters);
    RUN(test_matches_reference);
    RUN(test_dsf_and_dff);
    RUN(test_ultrasonic_rejected);
    RUN(test_rates_and_multiples);
    RUN(test_seeks_exact);
    RUN(test_dff_channel_order);
    RUN(test_dsf_channel_types);
    RUN(test_dsf_lengths);
    RUN(test_dff_cut_and_dst);
    RUN(test_tags);
    RUN(test_bad_headers);
    fa_engine_destroy(g_engine);
    if (g_failures) { fprintf(stderr, "%d failure(s)\n", g_failures); return 1; }
    printf("all DSD tests passed\n");
    return 0;
}
